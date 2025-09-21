using App.RabbitBuilder.Options;
using RabbitMQ.Client;

namespace App.RabbitBuilder.Repository
{
    public interface IRabbitRepository
    {
        Task<IConnection> CreateConnectionAsync(RabbitOptionsBase options);
        Task<IChannel> CreateChannelAsync(IConnection connection);
    }
}
