/*!
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License.
 */
namespace ProcessManager
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.EventGrid;
    using Microsoft.Azure.EventGrid.Models;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using StackExchange.Redis;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using ProcessManager.Libraries;
    using ProcessManager.Classes;
    using System.Diagnostics;
    using Microsoft.Azure.ServiceBus;
    using System.Text;
    public static class CacheConnectorUpsert
    {
        private const string LOGGING_SERVICE_NAME = "CacheConnectorUpsert";
        private const string LOGGING_SERVICE_VERSION = "1.0";

        private const string EVENT_GRID_TOPIC_URI_VARIABLE_NAME = "EVENT_GRID_TOPIC_URI";
        private const string EVENT_GRID_KEY_VARIABLE_NAME = "EVENT_GRID_KEY";
        private const string SERVICE_BUS_CONNECTION_STRING_VARIABLE_NAME = "SERVICE_BUS_CONNECTION_STRING";
        private const string BACKEND_STATUS_CREATED = "created";
        private const string BACKEND_STATUS_COMPLETED = "completed";
        private const string BACKEND_STATUS_RUNNING = "running";
        private const string BACKEND_STATUS_FAILED = "failed";

        private const string GRID_PUBLISH_RECORD_KEY = "ORIG";

        [FunctionName("CacheConnectorUpsert")]
        public static async Task<IActionResult> TaskRun([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req, ILogger logger)
        {
            IDatabase db = null;
            AppInsightsLogger appInsightsLogger = new AppInsightsLogger(logger, LOGGING_SERVICE_NAME, LOGGING_SERVICE_VERSION);

            var redisOperation = "insert";

            APITask task = null;

            if (req.Body != null)
            {
                string body = string.Empty;
                try
                {
                    using (StreamReader reader = new StreamReader(req.Body))
                    {
                        if (reader.BaseStream.Length > 0)
                        {
                            body = reader.ReadToEnd();

                            if (body.StartsWith("["))
                            {
                                task = JsonConvert.DeserializeObject<APITask[]>(body)[0];
                            }
                            else
                            {
                                task = JsonConvert.DeserializeObject<APITask>(body);
                            }
                        }
                        else
                        {
                            appInsightsLogger.LogWarning("Parameters missing. Unable to create task.");
                            return new BadRequestResult();
                        }
                    }
                }
                catch (Exception ex)
                {
                    appInsightsLogger.LogInformation(ex.Message + ex.StackTrace.ToString());
                    appInsightsLogger.LogError(ex);
                    appInsightsLogger.LogRedisUpsert("Redis upsert failed.", redisOperation, task.Timestamp, body);
                    return new StatusCodeResult(500);
                }
            }
            else
            {
                appInsightsLogger.LogInformation("Parameters missing. Unable to create task.");
                appInsightsLogger.LogWarning("Parameters missing. Unable to create task.");
                return new BadRequestResult();
            }

            if (!String.IsNullOrWhiteSpace(task.TaskId))
            {
                appInsightsLogger.LogInformation("Updating status", task.Endpoint, task.TaskId);
                redisOperation = "update";
            }
            else
            {
                task.TaskId = Guid.NewGuid().ToString();
            }

            task.Timestamp = DateTime.UtcNow.ToString();

            try
            {
                db = RedisConnection.GetDatabase();
            }
            catch (Exception ex)
            {
                appInsightsLogger.LogInformation(ex.Message + ex.StackTrace.ToString());
                appInsightsLogger.LogError(ex, task.Endpoint, task.TaskId);
                appInsightsLogger.LogRedisUpsert("Redis upsert failed.", redisOperation, task.Timestamp, task.Endpoint, task.TaskId);
                return new StatusCodeResult(500);
            }

            string serializedTask = string.Empty;
            try
            {
                var taskBody = task.Body;
                task.Body = null;

                serializedTask = JsonConvert.SerializeObject(task);
                RedisValue res = RedisValue.Null;

                var upsertTransaction = db.CreateTransaction();

                upsertTransaction.StringSetAsync(task.TaskId, serializedTask);

                // Get seconds since epoch
                TimeSpan ts = (DateTime.UtcNow - new DateTime(1970, 1, 1));
                int timestamp = (int)ts.TotalSeconds;

                upsertTransaction.SortedSetAddAsync(string.Format("{0}_{1}", task.EndpointPath, task.BackendStatus), new SortedSetEntry[] { new SortedSetEntry(task.TaskId, timestamp) });

                if (task.BackendStatus.Equals(BACKEND_STATUS_RUNNING))
                {
                    upsertTransaction.SortedSetRemoveAsync(string.Format("{0}_{1}", task.EndpointPath, BACKEND_STATUS_CREATED), task.TaskId);
                }
                else if (task.BackendStatus.Equals(BACKEND_STATUS_COMPLETED) || task.BackendStatus.Equals(BACKEND_STATUS_FAILED))
                {
                    upsertTransaction.SortedSetRemoveAsync(string.Format("{0}_{1}", task.EndpointPath, BACKEND_STATUS_RUNNING), task.TaskId);
                }

                bool isSubsequentPipelineCall = false;

                bool isPublish = false;
                bool.TryParse(task.PublishToGrid.ToString(), out isPublish);

                if (isPublish == true || task.PublishToGrid == true)
                {
                    if (string.IsNullOrEmpty(taskBody))
                    {
                        // This is a subsequent pipeline publish.
                        isSubsequentPipelineCall = true;
                    }
                    else
                    {
                        upsertTransaction.StringSetAsync(string.Format("{0}_{1}", task.TaskId, GRID_PUBLISH_RECORD_KEY), taskBody);
                    }
                }

                var watch = Stopwatch.StartNew();
                if (await upsertTransaction.ExecuteAsync() == false)
                {
                    var ex = new Exception("Unable to complete redis transaction.");
                    appInsightsLogger.LogError(ex, task.Endpoint, task.TaskId);
                    throw ex;
                }
                watch.Stop();
                appInsightsLogger.LogInformation(string.Format("ExecuteAsync duration: {0}", watch.ElapsedMilliseconds), task.Endpoint, task.TaskId);

                if (isSubsequentPipelineCall)
                {
                    // We have to get the original body, since it's currently empty.
                    taskBody = await db.StringGetAsync(string.Format("{0}_{1}", task.TaskId, GRID_PUBLISH_RECORD_KEY));
                }

                if (task.PublishToGrid)
                {
                    watch.Restart();
                    if (await PublishEvent(task, taskBody, appInsightsLogger) == false)
                    {
                        // Move task to failed
                        var updateTransaction = db.CreateTransaction();
                        task.Status = "Failed - unable to send to backend service.";
                        task.BackendStatus = BACKEND_STATUS_FAILED;
                        string updateBody = JsonConvert.SerializeObject(task);

                        updateTransaction.StringSetAsync(task.TaskId, updateBody);
                        updateTransaction.SortedSetAddAsync(string.Format("{0}_{1}", task.EndpointPath, task.BackendStatus), new SortedSetEntry[] { new SortedSetEntry(task.TaskId, timestamp) });
                        updateTransaction.SortedSetRemoveAsync(string.Format("{0}_{1}", task.EndpointPath, BACKEND_STATUS_CREATED), task.TaskId);

                        if (await updateTransaction.ExecuteAsync() == false)
                        {
                            var ex = new Exception("Unable to complete redis transaction.");
                            appInsightsLogger.LogError(ex, task.Endpoint, task.TaskId);
                            throw ex;
                        }
                    }
                    watch.Stop();
                    appInsightsLogger.LogInformation(string.Format("PublishEvent duration: {0}", watch.ElapsedMilliseconds), task.Endpoint, task.TaskId);
                }
            }
            catch (Exception ex)
            {
                appInsightsLogger.LogInformation(ex.Message + ex.StackTrace.ToString());
                appInsightsLogger.LogError(ex, task.Endpoint, task.TaskId);
                appInsightsLogger.LogRedisUpsert("Redis upsert failed.", redisOperation, task.Timestamp, serializedTask, task.Endpoint, task.TaskId);
                return new StatusCodeResult(500);
            }

            return new OkObjectResult(serializedTask);
        }

        /// <summary>
        /// Publishes the event to be processed.
        /// Depending on configuration sends to Event Grid or Service Bus
        /// </summary>
        /// <returns></returns>
        private static async Task<bool> PublishEvent(APITask task, string taskBody, AppInsightsLogger appInsightsLogger)
        {
            var eventGridTopicUri = Environment.GetEnvironmentVariable(EVENT_GRID_TOPIC_URI_VARIABLE_NAME, EnvironmentVariableTarget.Process);
            var eventGridKey = Environment.GetEnvironmentVariable(EVENT_GRID_KEY_VARIABLE_NAME, EnvironmentVariableTarget.Process);
            if (string.IsNullOrEmpty(eventGridTopicUri) || string.IsNullOrEmpty(eventGridKey))
            {
                return await PublishServiceBusQueueEvent(task, taskBody, appInsightsLogger);
            }
            else
            {
                return await PublishEventGridEvent(task, taskBody, eventGridTopicUri, eventGridKey, appInsightsLogger);
            }
        }

        private static async Task<bool> PublishEventGridEvent(APITask task, string taskBody, string eventGridTopicUri, string eventGridKey, AppInsightsLogger appInsightsLogger)
        {
            var ev = new EventGridEvent()
            {
                Id = task.TaskId,
                EventType = "task",
                Data = taskBody,
                EventTime = DateTime.Parse(task.Timestamp),
                Subject = task.Endpoint,
                DataVersion = "1.0"
            };

            string topicHostname = new Uri(eventGridTopicUri).Host;
            TopicCredentials topicCredentials = new TopicCredentials(eventGridKey);
            EventGridClient client = new EventGridClient(topicCredentials);

            try
            {
                await client.PublishEventsAsync(topicHostname, new List<EventGridEvent>() { ev });
            }
            catch (Exception ex)
            {
                appInsightsLogger.LogError(ex, task.Endpoint, task.TaskId);
                return false;
            }

            return true;
        }

        private static async Task<bool> PublishServiceBusQueueEvent(APITask task, string taskBody, AppInsightsLogger appInsightsLogger)
        {
            string serviceBusConnectionString = Environment.GetEnvironmentVariable(SERVICE_BUS_CONNECTION_STRING_VARIABLE_NAME, EnvironmentVariableTarget.Process);
            
            // A queue must be created for each endpoint, ex:
            // The queue name for http://52.224.89.22/v1/paws/consolidate should be http52.224.89.22v1pawsconsolidate
            var queueName = task.Endpoint.Replace(".", string.Empty);
            queueName = queueName.Replace("/", string.Empty);
            queueName = queueName.Replace(":", string.Empty);
            

            var messageProperties = new Dictionary<string,object>
            {
                { "TaskId", task.TaskId },
                { "Uri", task.Endpoint }
            };

            IQueueClient queueClient = new QueueClient(serviceBusConnectionString, queueName);

            try
            {
                Message message = new Message(Encoding.UTF8.GetBytes(taskBody));
                message.UserProperties["TaskId"] = task.TaskId;
                message.UserProperties["Uri"] = task.Endpoint;

                // Write the body of the message to the console.
                Console.WriteLine($"Sending taskId {task.TaskId} to queue {queueName}");

                // Send the message to the queue.
                await queueClient.SendAsync(message);
                await queueClient.CloseAsync();
                appInsightsLogger.LogInformation($"Sent task {task.TaskId} to queue {queueName}.", task.Endpoint, task.TaskId);
            }
            catch (Exception ex)
            {
                appInsightsLogger.LogError(ex, task.Endpoint, task.TaskId);
                return false;
            }

            return true;
        }

        private static void LogSetCount(string setName, APITask task, IDatabase db, AppInsightsLogger appInsightsLogger)
        {
            var len = db.SortedSetLength(setName);
            appInsightsLogger.LogMetric(setName, len, task.EndpointPath);
        }
    }
}