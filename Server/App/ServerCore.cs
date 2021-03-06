﻿#region copyright
/// GladNet Copyright (C) 2014 Andrew Blakely 
/// andrew.blakely@ymail.com
/// GitHub: HeIIoKitty
/// Unity3D: Glader
/// Please refer to the repo License file for licensing information
/// If this source code has been distributed without a copy of the original license file then this is an illegal copy and you should delete it
#endregion
using Common.Exceptions;
using GladNet.Common.Serializers;
using GladNet.Common;
using GladNet.Server.Connections;
using GladNet.Server.Connections.Readers;
using GladNet.Server.Logging;
using Lidgren.Network;
using ProtoBuf;
using ProtoBuf.Meta;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GladNet.Server
{

	public class CoreAttribute : Attribute
	{
		//Empty
		//This is just used to easily find the class that implements this base class in loaded assemblies.
	}

	//TODO: The class requires MASSIVE refactoring
	[CoreAttribute]
	/// <summary>
	/// This class represents the core/central point exposed to the user of the library. A class should implement this core class otherwise nothing can actually occur on the server.
	/// </summary>
	public abstract class ServerCore : ILoggable //MessageReciever, ILoggable
	{
		/// <summary>
		/// Forces the application's core to provide implementation for a logger class to log information to.
		/// </summary>
		public ILogger ClassLogger { get; private set; }

		private bool isReady = false;

		#region Connection collection properites and objects
		public IReadOnlyList<ServerPeer> ServerPeers
		{
			get { return ServerConnections.ToListOfPeer(); }
		}

		public IReadOnlyList<ClientPeer> ClientPeers
		{
			get { return Clients.ToListOfPeer(); }
		}

		private readonly IList<ConnectionResponse> UnhandledServerConnections;
		private readonly IConnectionCollection<ClientPeer, NetConnection> Clients;
		private readonly IConnectionCollection<ServerPeer, NetConnection> ServerConnections;
		#endregion

		private readonly NetServer lidgrenServerObj;

		public readonly string ExpectedClientHailMessage;

		//private readonly SerializationManager SerializerRegister;

		//private readonly PacketConverter Converter;

		public int Port { get; protected set; }
		
		/// <summary>
		/// Indicates the type of server. This is to distinguish between incoming and outgoing connections in server to server scenarios.
		/// Without this being considered during setup you won't be able to differentiate between a connection request from a client, or another subserver, from a server.
		/// </summary>
		protected abstract byte ServerTypeUniqueByte { get; }

		protected PacketHandler NetworkMessageHandler;

		//TODO: Refactor
		public ServerCore(ILogger loggerInstance, string appName, int port, string hailMessage) 
			: base()
		{
			NetworkMessageHandler = new PacketHandler(loggerInstance);
			NetworkMessageHandler.Register<DiffieHellmanAESEncryptor>(EncryptionBase.DefaultByte);

			Port = port;
			UnhandledServerConnections = new List<ConnectionResponse>();

			//Register the default serializer
			this.NetworkMessageHandler.Register<GladNetProtobufNetSerializer>();

			ClassLogger = loggerInstance;
			Clients = new ConnectionCollection<ClientPeer, NetConnection>();
			ServerConnections = new ConnectionCollection<ServerPeer, NetConnection>();

			//Construction of the Lidgren server listener
			NetPeerConfiguration config = new NetPeerConfiguration(appName);
			config.Port = port;

			//Server needs a much larger than default buffer size because corruption can happen otherwise
			config.ReceiveBufferSize = 500000;
			config.SendBufferSize = 500000;

			//This message type must be set to true so we can manage connections with hailmessages and so that we can
			//reject the majority of non-malicious malformed connection attempts
			config.SetMessageTypeEnabled(NetIncomingMessageType.ConnectionApproval, true);
			config.SetMessageTypeEnabled(NetIncomingMessageType.ExternalHighlevelMessage, true);

			//Reduces GC
			config.UseMessageRecycling = true;

			lidgrenServerObj = new NetServer(config);

			ExpectedClientHailMessage = hailMessage;
		}

		/// <summary>
		/// Called internally when a client attempts to connect to this server passing in details about the connection.
		/// </summary>
		/// <param name="request">A package of details about the client attempting to connect to the server.</param>
		/// <returns></returns>
		protected abstract ClientPeer OnAttemptedConnection(ConnectionRequest request);

		/// <summary>
		/// Called internally when this application recieves a success response to its connection attempt to another server. Servers connectiong to this application are passed into OnAttemptedConnection instead
		/// This internally creates a client object on the server that acts as a client to that server on a seperate port while other servers who choose instead to connect to us are represented by a seperate server object
		/// internally that listens on a different port.
		/// </summary>
		/// <param name="response">Response from the server that connection was attempted from.</param>
		/// <returns>A ServerPeer base instance for the other server to be managed as or null if for some reason the accepted connection is rejected.</returns>
		protected abstract ServerPeer OnConnectionSuccess(ConnectionResponse response);

		/// <summary>
		/// Attempts to connect to another server application from this server core/application.
		/// </summary>
		/// <param name="endPoint"></param>
		/// <param name="applicationName"></param>
		/// <param name="hailMessage"></param>
		/// <param name="callbackObject">Object that will be returned in the response. (Not sent to the server)</param>
		/// <returns>Indicates whether a connection has been successfully attempted (doesn't indicate if it was established).</returns>
		public bool ConnectToServer(IPEndPoint endPoint, string hailMessage, object callbackObject = null)
		{
			if (endPoint == null)
				return false;
			else
			{
				var msg = lidgrenServerObj.CreateMessage();
				msg.Write(hailMessage);
				msg.Write(ServerTypeUniqueByte);

#if DEBUGBUILD
				this.ClassLogger.LogDebug("Attempting to connect to another server.");
#endif
				NetConnection possibleConnection = lidgrenServerObj.Connect(endPoint, msg);

				ConnectionResponse response = new ConnectionResponse(endPoint, possibleConnection, hailMessage, callbackObject);

				this.UnhandledServerConnections.Add(response);
				return true;
			}
		}

		/// <summary>
		/// Is called if the connection attempt resulted in a failure the remote connection point was unreachable or did not respond.
		/// </summary>
		/// <param name="response">Response information that may have been gathered upon the failure.</param>
		protected virtual void OnConnectionFailure(ConnectionResponse response)
		{
#if DEBUGBUILD
			//TODO: Add information to the debug logger string for the response.
			ClassLogger.LogDebug("Failed to connect to a server.");
#endif
			//Just fail silently if not overidden.
		}

		/// <summary>
		/// Internally called by the app when it's loaded. Cannot be overidden and cannot be remove.
		/// </summary>
		internal void InternalOnStartup()
		{
			try
			{
				//Internally we should register Protobuf-net packets here.
				
				Packet.Register(typeof(EmptyPacket), true);
				Packet.Register(typeof(EncryptionRequest), true);
				Packet.Register(typeof(MalformedPacket), true);

				//Add in all the custom packets the server uses.
				RegisterProtobufPackets(Packet.Register);
				Packet.SetupProtoRuntimePacketInheritance();
				Packet.LockInProtobufnet();

				lidgrenServerObj.Start();
				
				OnStartup();

				//Sets the server to a ready state to begin polling and accepting connections.
				isReady = true;
			}
			//No reason to catch Loggable exceptions because if it reaches this point up the stack then the application is lost
			//Loggable and recoverable exceptions should be caught before this point.
			catch(SocketException e)
			{
				this.ClassLogger.LogError("Failed to start server. This may indicate that the listener failed to start on port: "
							+ this.Port + ". This could be because the port is already in use.");
			}
			catch (Exception e)
			{
				this.ClassLogger.LogError(e.Message + e.InnerException != null ? "InnerException: " + e.InnerException : "" +
					"This is an unrecoverable exception.");
			}
		}

		protected abstract void RegisterProtobufPackets(Func<Type, bool> registerAsDefaultFunc);

		//TODO: Totally refactor this garbage
		public void StartPipeListener(string clientHandleString)
		{
#if DEBUGBUILD
			ClassLogger.LogDebug("Started pipe listener to launcher with handle: " + clientHandleString);
#endif

			Task.Factory.StartNew(() =>
			{
				try
				{
					using (PipeStream ps = new AnonymousPipeClientStream(PipeDirection.In, clientHandleString))
					{
						using (StreamReader reader = new StreamReader(ps, Encoding.Default))
						{
							string message;
							while (true)
							{
								message = reader.ReadLine();

								if (message != null)
								{
#if DEBUGBUILD
									ClassLogger.LogDebug("Recieved message via Launcher pipe: " + message);
#endif


									if (message.Contains("[SHUTDOWN]"))
										if (message == "[SHUTDOWN] " + Process.GetCurrentProcess().Id.ToString())
										{
											isReady = false;
											break;
										}
								}
							}
						}
					}
				}
				catch(Exception e)
				{
					ClassLogger.LogError(e.Message);
				}
			}, TaskCreationOptions.LongRunning);
		}

		/// <summary>
		/// Internally called by the app when a shutdown request has been recieved.
		/// </summary>
		internal void InternalOnShutdown()
		{
			
#if DEBUGBUILD
			ClassLogger.LogDebug("Server shutdown requested.");
#endif
			OnShutdown();

			//Do not stop polling the server until the application is about to stop.
			//In case it is desired to send a final message to clients and subservers we flush the network queue
			//So these messages can hopefully be sent before shutdown.
			this.lidgrenServerObj.FlushSendQueue();
			isReady = false;
		}

		protected abstract void OnStartup();
		protected abstract void OnShutdown();

		/// <summary>
		/// This method should be called and serviced on the main thread or for Unity3D in a coroutine.
		/// </summary>
		public void Poll()
		{
			while(isReady)
			{
				MessagePoll(lidgrenServerObj);
			}
		}

		private void MessagePoll(NetPeer peer)
		{
			//TODO: Examine if this will cause latency issues for handling messages
			//This is done so that CPU usage isn't too high. It blocks the thread and waits for a message
			//Nothing but GladNet should be executing on the main thread anyway.
			NetIncomingMessage msg = peer.WaitMessage(10);

			//TODO: Refactor
			if (msg != null && msg.SenderConnection != null)
			{
#if DEBUGBUILD
				ClassLogger.LogDebug("Recieved a message from client ID: " + msg.SenderConnection.RemoteUniqueIdentifier);
#endif
				switch (msg.MessageType)
				{
					case NetIncomingMessageType.ConnectionApproval:
						ClassLogger.LogDebug("Hit connection approval");
						if (this.NetworkMessageHandler.TryReadHailMessage(msg, ExpectedClientHailMessage))
						{
							ClassLogger.LogDebug("About to approve.");
							try
							{
								msg.SenderConnection.Approve();
								this.TryRegisterApprovedConnection(msg.SenderConnection, msg.SenderConnection.RemoteHailMessage.ReadByte());
							}
							catch (NetException e)
							{
#if DEBUGBUILD
								ClassLogger.LogError("Failed to read connection type byte from hail message packet. Exception: " + e.Message);
#endif
							}
						}
						else
							ClassLogger.LogWarn("Client failed to satisfy hailmessage. Expected: " + ExpectedClientHailMessage);

						break;
					case NetIncomingMessageType.StatusChanged:
						//Malicious user could try to send a fake StatusChange without the extra byte so try to catch NetException.
						try
						{
							if (Clients.HasKey(msg.SenderConnection.RemoteUniqueIdentifier))
								this.ReadStatusChange((NetConnectionStatus)msg.ReadByte(), Clients[msg.SenderConnection.RemoteUniqueIdentifier]);
							else if (ServerConnections.HasKey(msg.SenderConnection.RemoteUniqueIdentifier))
								this.ReadStatusChange((NetConnectionStatus)msg.ReadByte(), ServerConnections[msg.SenderConnection.RemoteUniqueIdentifier]);
							else
								//If this point is reached it indicates that the status change is not from a registered connection and
								//this could indicate potentially a subserver connecting or maybe a client message before hail approval.
								ReadStatusChange((NetConnectionStatus)msg.ReadByte(), msg.SenderConnection);
						}
						catch (NetException e)
						{
#if DEBUGBUILD
							ClassLogger.LogError("NetConnection ID: " + msg.SenderConnection.RemoteUniqueIdentifier + " sent a potentially malicious StatusChange update. Error: " + e.Message);
#endif
						}
						break;

					case NetIncomingMessageType.ExternalHighlevelMessage:
						OnRecieveMessage(msg, false);
						break;
					case NetIncomingMessageType.Data:
						OnRecieveMessage(msg, true);
						break;
				}

				peer.Recycle(msg);
			}
		}

		protected virtual void OnRecieveMessage(NetIncomingMessage msg, bool isInternal)
		{
#if DEBUGBUILD
			ClassLogger.LogDebug("Recieved a high level message from client ID: " + msg.SenderConnection.RemoteUniqueIdentifier);
#endif

			if (Clients.HasKey(msg.SenderConnection.RemoteUniqueIdentifier)) //Client sent the message
				NetworkMessageHandler.DispatchMessage(Clients[msg.SenderConnection.RemoteUniqueIdentifier].HighlevelPeer, msg, isInternal);
			else if (ServerConnections.HasKey(msg.SenderConnection.RemoteUniqueIdentifier)) //Subserver sent a message
				NetworkMessageHandler.DispatchMessage(ServerConnections[msg.SenderConnection.RemoteUniqueIdentifier].HighlevelPeer, msg, isInternal);
			else
				ClassLogger.LogWarn("Recieved highlevel message with no receving object.");

			//At this point the message is for nobody and we shouldn't have recieved it.
			//In a perfect world we'd disconnect whoever sent it but we can't be sure a real client actually sent it
			//The package could be faked so just drop it.
		}

		//TODO: Address memory leak; not critical atm.
		private void ReadStatusChange(NetConnectionStatus netConnectionStatus, NetConnection netConnection)
		{
#if DEBUGBUILD
			this.ClassLogger.LogDebug("Entered into un-collected connection status handler.");
#endif

			//TODO: This whole method won't work if we don't achieve the connection and the list will continue to grow but it's not an imperative issue atm.
			ConnectionResponse cr = this.UnhandledServerConnections
				.FirstOrDefault(x => x.InternalNetConnection.RemoteUniqueIdentifier == netConnection.RemoteUniqueIdentifier);
			switch(netConnectionStatus)
			{
				case NetConnectionStatus.Disconnected:
					//If we get a disconnection and it's about an unhandled server that has yet to fully connect we should remove it.
					if(cr == null)
						return;

					UnhandledServerConnections.Remove(cr);
					this.OnConnectionFailure(cr);
					break;
				case NetConnectionStatus.Connected:
					if(cr == null)
						return;
#if DEBUGBUILD
					if(UnhandledServerConnections.Remove(cr))
						ClassLogger.LogDebug("Successfully removed a CR from a outgoing connection.");
					else
						ClassLogger.LogDebug("Failed to remove a CR from a failed outgoing connection.");
#else
					UnhandledServerConnections.Remove(cr);
#endif
					BuildServerPeer(netConnection, cr, ServerConnections);
					break;
			}
		}

		private ServerPeer BuildServerPeer(NetConnection netConnection, ConnectionResponse response, 
			IRegisterable<ConnectionPair<NetConnection, ServerPeer>, long> serverRegistery)
		{
			ServerPeer newPeer = this.OnConnectionSuccess(response);

			//If this is not null it means the application created a new serverpeer and it needs to be added
			//To the collection to allow for message forwarding and such.
			if (newPeer != null)
				serverRegistery.Register(new ConnectionPair<NetConnection, ServerPeer>(netConnection, newPeer),
					netConnection.RemoteUniqueIdentifier);

			return newPeer;
		}

		private void ReadStatusChange<PeerType>(NetConnectionStatus status, ConnectionPair<NetConnection, PeerType> peerPair)
			where PeerType : Peer
		{
			switch(status)
			{
				case NetConnectionStatus.Disconnected:
					peerPair.HighlevelPeer.Disconnect();
					break;
			}
		}

		private bool TryRegisterApprovedConnection(NetConnection netConnection, byte connectionType)
		{
			ClientPeer cp = OnAttemptedConnection(new ConnectionRequest(netConnection.RemoteEndPoint, netConnection.RemoteUniqueIdentifier, 
				netConnection, connectionType));
			if (cp != null)
			{
				//A connecting subserver should be treated as a client connection. ServerPeers are peers that should be created
				//on the connected end, which can recieve events and responses, to its requests. So all approved connections should be added to the client
				//registry.
				ClassLogger.LogDebug("Connectiontype ID: " + connectionType);
#if DEBUGBUILD
				ClassLogger.LogDebug("Adding new client to ConnectionCollection. ID: " + cp.UniqueConnectionId);
#endif
				return Clients.Register(new ConnectionPair<NetConnection, ClientPeer>(netConnection, cp), netConnection.RemoteUniqueIdentifier);
			}
			else
				ClassLogger.LogDebug("ClientPeer generated for {0}:{1} ID: {2} is null.", netConnection.RemoteEndPoint.Address, netConnection.RemoteEndPoint.Port, netConnection.RemoteUniqueIdentifier);

			return false;
		}
	}
}
