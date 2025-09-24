using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    }
}
