using App.RabbitBuilder.Configuration;
using App.RabbitBuilder.Exceptions;
using App.RabbitBuilder.Options;
using App.RabbitBuilder.Repository;
using App.RabbitBuilder.Service.Base;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace App.RabbitBuilder.Service.Sender
{
    public class RabbitSenderService : RabbitServiceBase, IRabbitSenderService
    {
        private readonly ILogger<RabbitSenderService> _logger;
        public RabbitSenderService(IRabbitRepository repository,
            ILogger<RabbitSenderService> logger,
            ConfigurationOptions configurationOptions) : base(configurationOptions, repository, logger)
        { 
            _logger = logger;
        }

        /// <summary>
        /// Sends a message to a RabbitMQ queue using simple RabbitOptions configuration.
        /// </summary>
        /// <typeparam name="T">The type of message to send</typeparam>
        /// <param name="rabbitOptions">RabbitMQ connection and queue options</param>
        /// <param name="message">The message object to send</param>
        /// <param name="token">Cancellation token for the operation</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task InitSenderRabbitQueueAsync<T>(RabbitOptions rabbitOptions, T message, CancellationToken token) where T : class
        {
            if (rabbitOptions.SenderQueueName == null)
                throw new ArgumentNullException(nameof(rabbitOptions.SenderQueueName), "SenderQueueName cannot be null");

            await RetryConnection(async () =>
            {
                await CreateRabbitConnectionAsync(rabbitOptions, token);
                await InitSenderRabbitQueueHandlerAsync(rabbitOptions.SenderQueueName, message);
            }, token);
        }

        /// <summary>
        /// Sends a message to a specific RabbitMQ queue using extended RabbitOptions configuration.
        /// </summary>
        /// <typeparam name="T">The type of message to send</typeparam>
        /// <param name="rabbitOptions">Extended RabbitMQ connection options with multiple queues</param>
        /// <param name="message">The message object to send</param>
        /// <param name="rabbitQueueName">The name of the specific queue to send to</param>
        /// <param name="token">Cancellation token for the operation</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task InitSenderRabbitQueueAsync<T>(RabbitOptionsExtended rabbitOptions, T message, string rabbitQueueName, CancellationToken token) where T : class
        {
            QueueOptions? queueOptions = rabbitOptions.SenderQueues?.FirstOrDefault(pr => pr.Name == rabbitQueueName)
                ?? throw new ArgumentNullException($"Queue with name '{rabbitQueueName}' not found in SenderQueues.");

            await RetryConnection(async () =>
            {
                await CreateRabbitConnectionAsync(rabbitOptions, token);
                await InitSenderRabbitQueueHandlerAsync(queueOptions, message);
            }, token);
        }

        private async Task InitSenderRabbitQueueHandlerAsync<T>(QueueOptions queueOptions,
            T message) where T : class
        {
            try
            {
                ValidateConnection();
                await channel!.QueueDeclareAsync(
                    queue: queueOptions.QueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    noWait: false);

                string rabbitMessage = JsonSerializer.Serialize(message);
                byte[] encodeMessage = Encoding.UTF8.GetBytes(rabbitMessage);

                var properties = new BasicProperties
                {
                    Persistent = true
                };

                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: queueOptions.QueueName, 
                    mandatory: true,
                    basicProperties: properties,
                    body: encodeMessage);

                
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message to RabbitMQ queue {QueueName}: {ErrorMessage}",
                    queueOptions.QueueName, ex.Message);
                throw;
            }
        }
    }
}
