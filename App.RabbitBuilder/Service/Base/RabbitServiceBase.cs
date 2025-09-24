using App.RabbitBuilder.Configuration;
using App.RabbitBuilder.Exceptions;
using App.RabbitBuilder.Options;
using App.RabbitBuilder.Repository;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Runtime.CompilerServices;

namespace App.RabbitBuilder.Service.Base
{
    public abstract class RabbitServiceBase : IRabbitServiceBase
    {
        protected readonly ConfigurationOptions configurationOptions;

        protected readonly IRabbitRepository rabbitRepository;

        protected readonly ILogger logger;

        protected IChannel? channel;

        protected IConnection? connection;
        public RabbitServiceBase(ConfigurationOptions ConfigurationOptions,
            IRabbitRepository RabbitRepository, ILogger Logger)
        {
            configurationOptions = ConfigurationOptions;
            rabbitRepository = RabbitRepository;
            logger = Logger;
        }

        /// <summary>
        /// Attempts to establish a connection by invoking the specified connection delegate, retrying on failure up to
        /// a configured number of times.
        /// </summary>
        /// <remarks>This method retries the connection operation a number of times specified by the
        /// configuration options. If the maximum number of retries is reached without a successful connection, a <see
        /// cref="TooManyRetriesException"/> is thrown. Between retries, the method waits for a delay period defined in
        /// the configuration options.</remarks>
        /// <param name="connection">A delegate representing the connection operation to be executed.</param>
        /// <param name="token">A <see cref="CancellationToken"/> used to observe cancellation requests.</param>
        /// <returns></returns>
        /// <exception cref="TooManyRetriesException">Thrown if all retry attempts fail to establish a connection.</exception>
        protected virtual async Task RetryConnection(Func<Task> connection, CancellationToken token)
        {
            for (int attempts = 1; attempts <= configurationOptions.ServiceRetryCount; attempts++)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    await connection();
                    logger.LogInformation("RabbitMQ connected successfully on attempt {Attempt}", attempts);
                    return;
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("RabbitMQ connection cancelled");
                    throw; // Re-throw cancellation
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Attempt {Attempt}: RabbitMQ connection failed - {ErrorMessage}", attempts, ex.Message);

                    if (attempts == configurationOptions.ServiceRetryCount)
                    {
                        logger.LogError(ex, "All {MaxAttempts} connection attempts failed", configurationOptions.ServiceRetryCount);
                        throw new TooManyRetriesException($"All {configurationOptions.ServiceRetryCount} connection attempts failed");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(configurationOptions.ServiceRetryDelaySeconds), token);
                }
            }
        }
        /// <summary>
        /// Asynchronously creates a RabbitMQ connection and channel using the specified options.
        /// </summary>
        /// <remarks>This method establishes a connection to RabbitMQ and creates a channel for
        /// communication.  If the operation fails, any partially created resources are disposed of, and the exception
        /// is rethrown.</remarks>
        /// <param name="rabbitOptions">The configuration options used to establish the RabbitMQ connection.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests. The operation will be canceled if the token is triggered.</param>
        /// <returns></returns>
        protected virtual async Task CreateRabbitConnectionAsync(RabbitOptionsBase rabbitOptions, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(connection != null && connection.IsOpen && channel != null && channel.IsOpen)
                {
                    logger.LogDebug("RabbitMQ connection and channel are already open");
                    return;
                }
                logger.LogDebug("Creating RabbitMQ connection");
                connection = await rabbitRepository.CreateConnectionAsync(rabbitOptions);
                logger.LogDebug("RabbitMQ connection created successfully");
                logger.LogDebug("Creating RabbitMQ channel");
                channel = await rabbitRepository.CreateChannelAsync(connection);
                logger.LogDebug("RabbitMQ channel created successfully");

                channel.BasicAcksAsync += Channel_BasicAcksAsync;
                channel.BasicNacksAsync += Channel_BasicNacksAsync;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create RabbitMQ connection or channel : {ErrorMessage}", ex.Message);
                await DisposeRabbitConnectionAsync();
                throw;
            }
        }
        /// <summary>
        /// Handles the BasicNack event raised by the RabbitMQ channel.
        /// </summary>
        /// <remarks>This method is invoked when a message is negatively acknowledged (Nack) by the
        /// RabbitMQ broker. It performs necessary actions to handle the Nack event, such as logging or retrying the
        /// message.</remarks>
        /// <param name="sender">The source of the event, typically the RabbitMQ channel that raised the event.</param>
        /// <param name="event">The <see cref="RabbitMQ.Client.Events.BasicNackEventArgs"/> instance containing the event data.</param>
        /// <returns>A completed <see cref="Task"/> representing the asynchronous operation.</returns>
        private Task Channel_BasicNacksAsync(object sender, RabbitMQ.Client.Events.BasicNackEventArgs @event)
        {
            MessageNotSend();
            return Task.CompletedTask;
        }
        /// <summary>
        /// Handles the BasicAcks event raised by the RabbitMQ channel.
        /// </summary>
        /// <param name="sender">The source of the event, typically the RabbitMQ channel.</param>
        /// <param name="event">The event data containing information about the acknowledgment.</param>
        /// <returns>A completed task representing the asynchronous operation.</returns>
        private Task Channel_BasicAcksAsync(object sender, RabbitMQ.Client.Events.BasicAckEventArgs @event)
        {
            MessageSend();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Asynchronously disposes of the RabbitMQ connection and channel, if they are open.
        /// </summary>
        /// <remarks>This method ensures that the RabbitMQ connection and channel are properly closed and
        /// disposed of to release any associated resources. It is safe to call this method multiple times; subsequent
        /// calls will have no effect if the connection and channel are already disposed.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the
        /// connection and channel were successfully disposed.</returns>
        protected virtual async Task<bool> DisposeRabbitConnectionAsync()
        {
            if (connection != null)
            {
                if(connection.IsOpen)
                    await connection.CloseAsync();
                connection.Dispose();
                connection = null;
            }
            if (channel != null)
            {
                if(channel.IsOpen)
                    await channel.CloseAsync();
                channel.BasicAcksAsync -= Channel_BasicAcksAsync;
                channel.BasicNacksAsync -= Channel_BasicNacksAsync;
                channel.Dispose();
                channel = null;
            }
            return true;
        }

        protected virtual void ValidateConnection()
        {
            if (channel is null || !channel.IsOpen || connection is null || !connection.IsOpen)
            {
                throw new RabbitChannelNullException("RabbitMQ channel is null. Ensure connection is established before sending messages.");
            }
        }
        private void MessageSend()
        {
            logger.LogInformation("Message sent successfully");
        }
        private void MessageNotSend()
        {
            logger.LogInformation("Message not sent");
        }   

        public void Dispose()
        {
            if(connection != null)
            {
                connection.Dispose();
            }
            if(channel != null)
            {
                channel.BasicAcksAsync-= Channel_BasicAcksAsync;
                channel.BasicNacksAsync-=Channel_BasicNacksAsync;
                channel.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
