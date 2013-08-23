﻿// -----------------------------------------------------------------------
// <copyright file="Http2Client.cs" company="Microsoft">
// TODO: Update copyright text.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.Mentalis;
using Org.Mentalis.Security.Ssl;
using Owin.Types;
using SharedProtocol;
using SharedProtocol.EventArgs;
using SharedProtocol.Exceptions;
using SharedProtocol.Extensions;
using SharedProtocol.Framing;
using SharedProtocol.Handshake;
using SharedProtocol.Http11;
using SharedProtocol.IO;
using SharedProtocol.Utils;

namespace SocketServer
{
    using AppFunc = Func<IDictionary<string, object>, Task>;
    /// <summary>
    /// This class handles incoming clients. It can accept them, make handshake and choose how to give a response.
    /// It encouraged to response with http11 or http20 
    /// </summary>
    internal sealed class HttpConnectingClient : IDisposable
    {
        private const string IndexHtml = "\\index.html";
        private const string Root = "\\Root";
        private const string ClientSessionHeader = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
        //Remove file:// from Assembly.GetExecutingAssembly().CodeBase
        private static readonly string AssemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().CodeBase.Substring(8));

        private readonly SecureTcpListener _server;
        private readonly SecurityOptions _options;
        private readonly AppFunc _next;
        private Http2Session _session;
        private readonly IDictionary<string, object> _environment;        
        private readonly FileHelper _fileHelper;
        private readonly object _writeLock = new object();
        private readonly bool _useHandshake;
        private readonly bool _usePriorities;
        private readonly bool _useFlowControl;
        private readonly List<string> _listOfRootFiles;
        private readonly object _listWriteLock = new object();
        private AppFunc _upgradeDelegate;

        internal HttpConnectingClient(SecureTcpListener server, SecurityOptions options,
                                     AppFunc next, bool useHandshake, bool usePriorities, 
                                     bool useFlowControl, List<string> listOfRootFiles,
                                     IDictionary<string, object> environment)
        {
            _environment = environment;
            _listOfRootFiles = listOfRootFiles;
            _usePriorities = usePriorities;
            _useHandshake = useHandshake;
            _useFlowControl = useFlowControl;
            _server = server;
            _next = next;
            _options = options;
            _fileHelper = new FileHelper(ConnectionEnd.Server);
            
            //Provide this delegate from somewhere?
            _upgradeDelegate = UpgradeHandshaker.Handshake;
        }

        private IDictionary<string, object> MakeUpgradeEnvironment(DuplexStream incomingClient, string selectedProtocol)
        {
            //Http1 layer will call middle. middle will call upgrade delegate
            if (selectedProtocol == Protocols.Http1)
            {
                var upgradeEnv = new Dictionary<string, object>
                    {
                        {OwinConstants.Opaque.Upgrade, _upgradeDelegate},
                        {OwinConstants.Opaque.Stream, incomingClient},
                        //Provide canc token
                        {OwinConstants.Opaque.CallCancelled, CancellationToken.None}
                    };

                return upgradeEnv;
            }

            return new Dictionary<string, object>();
        }

        /// <summary>
        /// Accepts client and deals handshake with it.
        /// </summary>
        internal void Accept()
        {
            SecureSocket incomingClient;

            using (var monitor = new ALPNExtensionMonitor())
            {
                incomingClient = _server.AcceptSocket(monitor);
            }
            Http2Logger.LogDebug("New connection accepted");
            Task.Run(() => HandleAcceptedClient(incomingClient));
        }

        private void HandleAcceptedClient(SecureSocket incomingClient)
        {
            bool backToHttp11 = false;
            string selectedProtocol = Protocols.Http2;
            var environmentCopy = new Dictionary<string, object>(_environment);
            
            if (_useHandshake)
            {
                try
                {
                    if (_options.Protocol != SecureProtocol.None)
                    {
                        
                        // TODO Make securehandshaker methods static
                        // TODO refactor
                        environmentCopy["secureSocket"] = incomingClient;
                        environmentCopy["securityOptions"] = _options;
                        environmentCopy["end"] = ConnectionEnd.Server;

                        new SecureHandshaker(environmentCopy).Handshake();
                    }

                    selectedProtocol = incomingClient.SelectedProtocol;

                    // TODO investigate why selectedProtocol is null after Handshake;
                    if (selectedProtocol == null)
                    {
                        selectedProtocol = Protocols.Http1;
                        backToHttp11 = true;
                    }
                }
                catch (Http2HandshakeFailed ex)
                {
                    if (ex.Reason == HandshakeFailureReason.InternalError)
                    {
                        backToHttp11 = true;
                    }
                    else
                    {
                        incomingClient.Close();
                        Http2Logger.LogError("Handshake timeout. Client was disconnected.");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Http2Logger.LogError("Exception occurred. Closing client's socket. " + e.Message);
                    incomingClient.Close();
                    return;
                }
            }

            var clientStream = new DuplexStream(incomingClient, true);
            environmentCopy.AddRange(MakeUpgradeEnvironment(clientStream, selectedProtocol));

            try
            {
                HandleRequest(clientStream, selectedProtocol, backToHttp11, environmentCopy);
            }
            catch (Exception e)
            {
                Http2Logger.LogError("Exception occurred. Closing client's socket. " + e.Message);
                incomingClient.Close();
            }
        }

        private void HandleRequest(DuplexStream incomingClient, string alpnSelectedProtocol, 
                                   bool backToHttp11, IDictionary<string, object> environment)
        {
            //Server checks selected protocol and calls http2 or http11 layer
            if (backToHttp11 || alpnSelectedProtocol == Protocols.Http1)
            {
                Http2Logger.LogDebug("Ssl chose http11");
                
                //Http11 should get initial headers (they can contain upgrade) 
                //after it got headers it should call middleware. 
                //Environment should contain upgrade delegate
                //Http11Manager.Http11SendResponse(incomingClient);
                Http11ProtocolOwinAdapter.ProcessRequest(incomingClient, environment, _next);
                return;
            }

            //ALPN selected http2. No need to perform upgrade handshake.
            OpenHttp2Session(incomingClient, environment);
        }

        private async void OpenHttp2Session(DuplexStream incomingClient, IDictionary<string, object> environment)
        {
            Http2Logger.LogDebug("Handshake successful");
            _session = new Http2Session(incomingClient, ConnectionEnd.Server, _usePriorities, _useFlowControl, _next, environment);

            try
            {
                await _session.Start();
            }
            catch (Exception)
            {
                Http2Logger.LogError("Client was disconnected");
            }
        }

        public void Dispose()
        {
            if (_session != null)
            {
                _session.Dispose();
            }

            _fileHelper.Dispose();
        }
    }
}
