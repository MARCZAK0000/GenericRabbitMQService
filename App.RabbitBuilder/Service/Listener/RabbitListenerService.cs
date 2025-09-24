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
    public class RabbitListenerService : RabbitServiceBase, IRabbitListenerService
    {
        private readonly ILogger<RabbitListenerService> _logger;
        public RabbitListenerService(IRabbitRepository repository,
            ILogger<RabbitListenerService> logger,
            ConfigurationOptions configurationOptions) : base(configurationOptions, repository, logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Initializes a listener for a RabbitMQ queue and sets up a message processing hook.
        /// </summary>
        /// <remarks>This method establishes a connection to the RabbitMQ server and initializes a
        /// listener for the specified queue. The <paramref name="MessageHook"/> delegate is called asynchronously for
        /// each message received from the queue.</remarks>
        /// <typeparam name="T">The type of the message to be processed by the listener. Must be a reference type.</typeparam>
        /// <param name="rabbitOptions">The configuration options for connecting to RabbitMQ, including the queue name to listen to.</param>
        /// <param name="MessageHook">A delegate that processes messages of type <typeparamref name="T"/>. The delegate is invoked for each
        /// message received.</param>
        /// <param name="token">A <see cref="CancellationToken"/> that can be used to cancel the operation.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="rabbitOptions"/> or its <see cref="RabbitOptions.ListenerQueueName"/> property is
        /// <c>null</c>.</exception>
        public async Task InitListenerRabbitQueueAsync<T>(RabbitOptions rabbitOptions, Func<T, Task> MessageHook, CancellationToken token)
            where T : class
        {
            if (rabbitOptions.ListenerQueueName == null)
            {
                throw new ArgumentNullException(nameof(rabbitOptions.ListenerQueueName), "ListenerQueueName cannot be null.");
            }
            await RetryConnection(async () =>
            {
                await CreateRabbitConnectionAsync(rabbitOptions, token);
                await InitListenerRabbitQueueCoreAsync(rabbitOptions, rabbitOptions.ListenerQueueName, MessageHook);
            }, token);
        }

        /// <summary>
        /// Initializes a listener for a RabbitMQ queue and sets up a message processing hook.
        /// </summary>
        /// <remarks>This method establishes a connection to the specified RabbitMQ queue and configures
        /// it to invoke the provided <paramref name="MessageHook"/> for each incoming message. The connection attempt
        /// is retried in case of transient failures, respecting the provided <paramref name="token"/> for
        /// cancellation.</remarks>
        /// <typeparam name="T">The type of the message to be processed. Must be a reference type.</typeparam>
        /// <param name="rabbitOptions">The extended RabbitMQ configuration options, including queue definitions.</param>
        /// <param name="rabbitName">The name of the RabbitMQ queue to listen to. Must match a queue defined in <paramref name="rabbitOptions"/>.</param>
        /// <param name="MessageHook">A delegate that processes messages of type <typeparamref name="T"/>. The delegate is invoked for each
        /// message received.</param>
        /// <param name="token">A <see cref="CancellationToken"/> that can be used to cancel the operation.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">Thrown if a queue with the specified <paramref name="rabbitName"/> is not found in the <paramref
        /// name="rabbitOptions"/>.</exception>
        public async Task InitListenerRabbitQueueAsync<T>(RabbitOptionsExtended rabbitOptions, string rabbitName, Func<T, Task> MessageHook, CancellationToken token)
            where T : class
        {
            QueueOptions? queueOptions = rabbitOptions.ListenerQueues?.FirstOrDefault(q => q.Name == rabbitName);
            if (queueOptions == null)
            {
                throw new ArgumentException($"Queue with name '{rabbitName}' not found in ListenerQueues.");
            }
            await RetryConnection(async () =>
            {
                await CreateRabbitConnectionAsync(rabbitOptions, token);
                await InitListenerRabbitQueueCoreAsync(rabbitOptions, queueOptions, MessageHook);
            }, token);
        }

        /// <summary>
        /// Initializes and configures a RabbitMQ listener for the specified queue, enabling message consumption.
        /// </summary>
        /// <remarks>This method establishes a connection to RabbitMQ, declares the specified queue, and
        /// starts consuming messages from it. Messages are deserialized into the specified type <typeparamref
        /// name="T"/> and passed to the provided <paramref name="MessageHook"/> for processing. If an error occurs
        /// during message processing, the message is negatively acknowledged and not requeued.</remarks>
        /// <typeparam name="T">The type of the message model expected in the queue. This type must be a reference type.</typeparam>
        /// <param name="rabbitOptions">The RabbitMQ connection options, including host, port, and authentication details.</param>
        /// <param name="queueOptions">The configuration options for the target queue, such as the queue name and other properties.</param>
        /// <param name="MessageHook">A callback function to process messages received from the queue. The function is invoked with the
        /// deserialized message of type <typeparamref name="T"/>.</param>
        /// <returns></returns>
        private async Task InitListenerRabbitQueueCoreAsync<T>(RabbitOptionsBase rabbitOptions, QueueOptions queueOptions, Func<T, Task> MessageHook)
            where T : class
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
                        var messageModel = JsonSerializer.Deserialize<T>(message);
                        ArgumentNullException.ThrowIfNull(messageModel, "Message Null");
                        await MessageHook.Invoke(messageModel!);
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
