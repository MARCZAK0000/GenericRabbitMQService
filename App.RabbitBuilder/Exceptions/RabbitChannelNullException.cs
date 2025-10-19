namespace App.RabbitBuilder.Exceptions
{
    public class RabbitChannelNullException : Exception
    {
        public RabbitChannelNullException()
        {
        }

        public RabbitChannelNullException(string? message) : base(message)
        {

        }
        public RabbitChannelNullException(string methodName, string message) : base($"{methodName}: {message}")
        {

        }
    }
}
