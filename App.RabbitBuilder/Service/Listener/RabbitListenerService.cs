using App.RabbitBuilder.Configuration;
using App.RabbitBuilder.Exceptions;
using App.RabbitBuilder.Options;
using App.RabbitBuilder.Repository;
using App.RabbitBuilder.Service.Base;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace App.RabbitBuilder.Service.Listener
{
    public sealed class RabbitListenerService : RabbitServiceBase, IRabbitListenerService
    {
        private readonly ILogger<RabbitListenerService> _logger;
        public RabbitListenerService(IRabbitRepository repository,
            ILogger<RabbitListenerService> logger,
            ConfigurationOptions configurationOptions) : base(configurationOptions, repository, logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Initializes a listener queue for processing messages of the specified type asynchronously.
        /// </summary>
        /// <remarks>This method establishes a connection to RabbitMQ, initializes the listener queue, and
        /// sets up the specified message processing logic. The operation will retry the connection in case of transient
        /// failures.</remarks>
        /// <typeparam name="T">The type of the message to be processed. Must be a reference type with a parameterless constructor.</typeparam>
        /// <param name="rabbitOptions">The configuration options for the RabbitMQ connection and listener queue. The <see
        /// cref="RabbitOptions.ListenerQueueName"/> property must not be null.</param>
        /// <param name="MessageHook">A delegate that defines the asynchronous processing logic for each message received from the queue.</param>
        /// <param name="token">A <see cref="CancellationToken"/> that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="rabbitOptions"/> or its <see cref="RabbitOptions.ListenerQueueName"/> property is
        /// null.</exception>
        public async Task InitListenerQueueAsync<T>(RabbitOptions rabbitOptions, Func<T, Task> MessageHook, CancellationToken token)
            where T : class, new() 
        {
            if (rabbitOptions.ListenerQueueName == null)
            {
                throw new RabbitChannelNullException(nameof(rabbitOptions.ListenerQueueName), "ListenerQueueName cannot be null.");
            }
            await RetryConnection(async () =>
            {
                await CreateRabbitConnectionAsync(rabbitOptions, token);
                await InitListenerAsync(rabbitOptions, rabbitOptions.ListenerQueueName, MessageHook);
            }, token);
        }

       /// <summary>
       /// Initializes a listener queue for processing messages of the specified type.
       /// </summary>
       /// <remarks>This method establishes a connection to RabbitMQ, initializes the specified listener
       /// queue, and sets up the provided message processing callback. The operation will retry the connection if it
       /// fails, respecting the provided cancellation token.</remarks>
       /// <typeparam name="T">The type of the messages to be processed. Must be a reference type with a parameterless constructor.</typeparam>
       /// <param name="rabbitOptions">The extended RabbitMQ configuration options. This must include the listener queue definitions.</param>
       /// <param name="rabbitName">The name of the listener queue to initialize. The queue must be defined in <paramref name="rabbitOptions"/>.</param>
       /// <param name="MessageHook">A callback function that processes messages of type <typeparamref name="T"/>. The function is invoked for
       /// each message received.</param>
       /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
       /// <returns>A task that represents the asynchronous operation.</returns>
       /// <exception cref="ArgumentException">Thrown if a queue with the specified <paramref name="rabbitName"/> is not found in the listener queue
       /// definitions.</exception>
        public async Task InitListenerQueueAsync<T>(RabbitOptionsExtended rabbitOptions, string rabbitName, Func<T, Task> MessageHook, CancellationToken token)
            where T : class, new()
        {
            QueueOptions? queueOptions = rabbitOptions.ListenerQueues?.FirstOrDefault(q => q.Name == rabbitName);
            if (queueOptions == null)
            {
                throw new RabbitChannelNullException($"Queue with name '{rabbitName}' not found in ListenerQueues.");
            }
            await RetryConnection(async () =>
            {
                await CreateRabbitConnectionAsync(rabbitOptions, token);
                await InitListenerAsync(rabbitOptions, queueOptions, MessageHook);
            }, token);
        }

        /// <summary>
        /// Initializes and configures a RabbitMQ listener for the specified queue, enabling message consumption.
        /// </summary>
        /// <remarks>This method establishes a connection to RabbitMQ, declares the specified queue, and
        /// starts consuming messages from it. Messages are deserialized into the specified type
        /// <param name="rabbitOptions">The RabbitMQ connection options, including host, port, and authentication details.</param>
        /// <param name="queueOptions">The configuration options for the target queue, such as the queue name and other properties.</param>
        /// <param name="MessageHook">A callback function to process messages received from the queue. The function is invoked with the
        /// deserialized message of type <typeparamref name="T"/>.</param>
        /// <returns></returns>
        private async Task InitListenerAsync<T>(RabbitOptionsBase rabbitOptions, QueueOptions queueOptions, Func<T, Task> MessageHook)
            where T : class, new()
        {
            try
            {
                ValidateConnection();
                await channel!.QueueDeclareAsync(queue: queueOptions.QueueName,
                    durable: true, exclusive: false, autoDelete: false, arguments: null,
                        noWait: false);

                _logger.LogInformation
                    ("{Date} - ListenerQueue : Created Queue, on {host}, queue_name: {name}",
                    DateTime.Now, rabbitOptions.Host, queueOptions.QueueName);

                AsyncEventingBasicConsumer consumer = new(channel);
                consumer.ReceivedAsync += async (sender, ea) =>
                {
                    try
                    {
                        var body = ea.Body.ToArray();
                        var message = Encoding.UTF8.GetString(body);
                        var messageObj = JsonSerializer.Deserialize<T>(message);
                        ArgumentNullException.ThrowIfNull(message, "Message Null");
                        await MessageHook.Invoke(messageObj!);
                        await channel.BasicAckAsync(deliveryTag: ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception messageEx)
                    {
                        _logger.LogError("{Date} - Error processing message: {ex}", DateTime.Now, messageEx.Message);
                        await channel.BasicNackAsync(deliveryTag: ea.DeliveryTag, multiple: false, requeue: false);
                    }
                };

                await channel.BasicConsumeAsync(queueOptions.QueueName, autoAck: false, consumer: consumer);

                _logger.LogInformation("{Date} - ListenerQueue : Successfully started consuming messages from {queueName}",
                    DateTime.Now, queueOptions.QueueName);
            }
            catch (Exception ex)
            {
                _logger.LogError("{Date} - ListenerQueue : Failed to initialize RabbitMQ listener for queue {queueName}. Error: {ex}. Application will continue without RabbitMQ functionality.",
                    DateTime.Now, queueOptions?.QueueName ?? "Unknown", ex.Message);
                throw;
            }
        }
    }
}
