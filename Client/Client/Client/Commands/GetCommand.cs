using System;
using System.Configuration;
using Client.Commands;

namespace Client
{
    internal sealed class GetCommand : Command, IUriCommand
    {
        private Uri _uri;
        private readonly string _method;

        public Uri Uri
        {
            get { return _uri; }
        }

        public string Path { get { return _uri.PathAndQuery; } }
        public string Method { get { return _method; } }

        internal GetCommand(string cmdBody)
        {
            _method = "get";
            Parse(cmdBody);
        }

        protected override void Parse(string cmd)
        {
            if (!cmd.Substring(7).Contains(":"))
                throw new InvalidOperationException("Specify the port!");

            if (Uri.TryCreate(cmd, UriKind.Absolute, out _uri) == false)
            {
                throw new InvalidOperationException("Invalid Get command!");
            }
            int securePort;
            try
            {
                securePort = int.Parse(ConfigurationManager.AppSettings["securePort"]);
            }
            catch (Exception)
            {
                throw new InvalidOperationException("Invalid port in the config file");
            }

            if (Uri.Port == securePort
                && 
                Uri.Scheme == Uri.UriSchemeHttp
                ||
                Uri.Port != securePort
                && 
                Uri.Scheme == Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("Invalid scheme on port! Use https for secure port");
            }
        }

        internal override CommandType GetCmdType()
        {
            return CommandType.Get;
        }
    }
}
