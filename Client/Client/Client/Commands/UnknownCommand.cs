﻿namespace Client
{
    internal class UnknownCommand : Command
    {
        public string UnknownCmd { get; private set; }

        internal UnknownCommand(string cmd)
        {
            UnknownCmd = cmd;
        }

        internal override CommandType GetCmdType()
        {
            return CommandType.Unknown;
        }

        protected override void Parse(string cmd)
        {
            
        }
    }
}
