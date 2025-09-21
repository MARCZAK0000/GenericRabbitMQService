using App.RabbitBuilder.Configuration;
using App.RabbitBuilder.Repository;
using App.RabbitBuilder.Service.Listener;
using App.RabbitBuilder.Service.Sender;
using Microsoft.Extensions.DependencyInjection;

namespace App.RabbitBuilder.ServiceExtensions
{
    public static class AddRabbitMqExtensions
    {
        public static void AddRabbitService(this IServiceCollection services, Action<ConfigurationOptions>? configurationOptions = null)
        {
            var options = new ConfigurationOptions();
            configurationOptions?.Invoke(options);

            services.AddSingleton(options);
            services.AddScoped<IRabbitRepository, RabbitRepository>();  
            services.AddScoped<IRabbitListenerService,  RabbitListenerService>();   
            services.AddScoped<IRabbitSenderService, RabbitSenderService>();
        }
    }
}
