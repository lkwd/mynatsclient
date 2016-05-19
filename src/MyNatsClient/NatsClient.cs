using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NatsFun.Events;
using NatsFun.Internals;
using NatsFun.Internals.Extensions;
using NatsFun.Ops;

namespace NatsFun
{
    public class NatsClient : INatsClient, IDisposable
    {
        private static readonly ILogger Logger = LoggerManager.Resolve(typeof(NatsClient));

        private const string Crlf = "\r\n";
        private const int ConsumerMaxSpinWaitMs = 500;
        private const int ConsumerIfNoDataWaitForMs = 100;

        private readonly object _sync;
        private readonly ConnectionInfo _connectionInfo;
        private readonly Func<bool> _socketIsConnected;
        private readonly Func<bool> _consumerIsCancelled;
        private readonly Func<bool> _hasData;
        private NatsClientEventMediator _eventMediator;
        private NatsOpMediator _opMediator;
        private Socket _socket;
        private NetworkStream _readStream;
        private NatsOpStreamReader _reader;
        private Task _consumer;
        private CancellationTokenSource _consumerCancellation;
        private NatsServerInfo _serverInfo;
        private bool _isDisposed;

        public string Id => _connectionInfo.ClientId;
        public IObservable<IClientEvent> Events => _eventMediator;
        public IObservable<IOp> IncomingOps => _opMediator;
        public INatsClientStats Stats => _opMediator;
        public NatsClientState State { get; private set; }

        public NatsClient(ConnectionInfo connectionInfo)
        {
            _sync = new object();
            _connectionInfo = connectionInfo.Clone();
            _eventMediator = new NatsClientEventMediator();
            _opMediator = new NatsOpMediator();

            _socketIsConnected = () => _socket != null && _socket.Connected;
            _consumerIsCancelled = () => _consumerCancellation == null || _consumerCancellation.IsCancellationRequested;
            _hasData = () =>
                    _socketIsConnected() &&
                    _readStream != null &&
                    _readStream.CanRead &&
                    _readStream.DataAvailable;
            State = NatsClientState.Disconnected;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            _isDisposed = true;
        }

        private void Dispose(bool disposing)
        {
            if (_isDisposed || !disposing)
                return;

            Release();

            _eventMediator?.Dispose();
            _eventMediator = null;

            _opMediator?.Dispose();
            _opMediator = null;
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        public void Disconnect()
        {
            ThrowIfDisposed();

            DoDisconnect(DisconnectReason.ByConsumer);
        }

        private void DoDisconnect(DisconnectReason reason)
        {
            if (State != NatsClientState.Connected)
                return;

            lock (_sync)
            {
                if (State != NatsClientState.Connected)
                    return;

                Release();

                State = NatsClientState.Disconnected;
            }

            OnDisconnected(reason);
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            _eventMediator.Dispatch(new ClientDisconnected(this, reason));
        }

        private void OnFailed(ErrOp op)
        {
            _eventMediator.Dispatch(new ClientFailed(this, op));
        }

        public void Connect()
        {
            ThrowIfDisposed();

            if (State == NatsClientState.Connected || State == NatsClientState.Connecting)
                return;

            lock (_sync)
            {
                if (State == NatsClientState.Connected || State == NatsClientState.Connecting)
                    return;

                State = NatsClientState.Connecting;
                Release();

                try
                {
                    //TODO: Potentially track ping times and/or use statistical endpoints of each node to pick best suited
                    var hosts = new Queue<Host>(_connectionInfo.Hosts.GetRandomized());
                    while (hosts.Any())
                    {
                        if (TryConnectTo(hosts.Dequeue()))
                            break;
                    }

                    if (!_socketIsConnected())
                        throw NatsException.NoConnectionCouldBeMade();

                    State = NatsClientState.Connected;
                }
                catch (Exception)
                {
                    Release();
                    State = NatsClientState.Disconnected;
                    throw;
                }
            }
        }

        private bool TryConnectTo(Host host)
        {
            //TODO: Use async connect
            _socket = _socket ?? SocketFactory.Create();
            _socket.Connect(host.Address, host.Port);
            _readStream = new NetworkStream(_socket, FileAccess.Read, false);
            _reader = new NatsOpStreamReader(_readStream, _hasData);

            var op = TryGetInitialOp();
            if (op == null)
            {
                Logger.Error("Error while connecting. Expected to get INFO after connection. Got nothing in timely fashion.");
                return false;
            }

            var infoOp = op as InfoOp;
            if (infoOp == null)
            {
                Logger.Error($"Expected to get INFO after connection. Got {op.GetAsString()}.");
                return false;
            }

            _serverInfo = NatsServerInfo.Parse(infoOp);

            _opMediator.Dispatch(infoOp);

            //TODO: Auth...
            //TODO: SSL
            _socket.SendUtf8($"CONNECT {{\"lang\":\"csharp\",\"verbose\": {_connectionInfo.Verbose.ToString().ToLower()}}}{Crlf}");

            if (!_socket.Connected)
            {
                Logger.Error("No connection could be established with server.");
                return false;
            }

            _consumerCancellation = new CancellationTokenSource();
            _consumer = Task.Factory.StartNew(
                ConsumeStream,
                _consumerCancellation.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
                {
                    var errOp = t.Result;
                    if (errOp != null)
                    {
                        DoDisconnect(DisconnectReason.DueToFailure);
                        OnFailed(errOp);
                    }
                });

            return true;
        }

        private IOp TryGetInitialOp()
        {
            //TODO: Make pretty and use ConnectionInfo to determine how long time to try
            IOp op = null;
            for (var i = 0; i < 20; i++)
            {
                op = _reader.ReadOp().FirstOrDefault();
                if (op != null)
                    break;
                Thread.Sleep(100);
            }
            return op;
        }

        private ErrOp ConsumeStream()
        {
            ErrOp errOp = null;

            while (_socketIsConnected() && !_consumerIsCancelled() && errOp == null)
            {
                SpinWait.SpinUntil(() => !_socketIsConnected() || _consumerIsCancelled() || _hasData(), ConsumerMaxSpinWaitMs);
                if (!_socketIsConnected())
                    break;

                if (_consumerIsCancelled())
                    break;

                if (!_hasData())
                {
                    Thread.Sleep(ConsumerIfNoDataWaitForMs);
                    continue;
                }

                foreach (var op in _reader.ReadOp())
                {
                    if (_connectionInfo.AutoRespondToPing && op is PingOp)
                        Pong();

                    errOp = op as ErrOp;
                    _opMediator.Dispatch(op);
                }
            }

            return errOp;
        }

        private void Release()
        {
            lock (_sync)
            {
                Try.All(
                    () =>
                    {
                        _consumerCancellation?.Cancel();
                        _consumerCancellation?.Dispose();
                        _consumerCancellation = null;
                    },
                    () =>
                    {
                        if (_consumer == null || !_consumer.IsCompleted)
                            return;

                        _consumer.Dispose();
                        _consumer = null;
                    },
                    () =>
                    {
                        _reader?.Dispose();
                        _reader = null;
                    },
                    () =>
                    {
                        _readStream?.Close();
                        _readStream?.Dispose();
                        _readStream = null;
                    },
                    () =>
                    {
                        _socket?.Shutdown(SocketShutdown.Both);
                        _socket?.Close();
                        _socket?.Dispose();
                        _socket = null;
                    });

                _serverInfo = null;
            }
        }

        public void Pub(string subject, string data)
        {
            //TODO: provide overload for other then string as data

            ThrowIfDisposed();

            DoSend($"PUB {subject} {data.Length}{Crlf}{data}{Crlf}");
        }

        public void Sub(string subject, string subscriptionId)
        {
            ThrowIfDisposed();

            DoSend($"SUB {subject} {subscriptionId}@{Id}{Crlf}");
        }

        public void UnSub(string subscriptionId, int? maxMessages = null)
        {
            ThrowIfDisposed();

            DoSend($"UNSUB {subscriptionId}@{Id} {maxMessages}{Crlf}");
        }

        public void Ping()
        {
            ThrowIfDisposed();

            DoSend($"PING{Crlf}");
        }

        public void Pong()
        {
            ThrowIfDisposed();

            DoSend($"PONG{Crlf}");
        }

        public void Send(string data)
        {
            ThrowIfDisposed();

            DoSend(data);
        }

        private void DoSend(string data)
        {
            if (State != NatsClientState.Connected)
                throw new InvalidOperationException($"Can not send. Client is not {NatsClientState.Connected}");

            //TODO: Have a write stream as we do have a read stream.
            //That way we could flush after the first few bytes since the nats server terminates as soon as it understands it's an invalid command.

            var buffer = Encoding.UTF8.GetBytes(data);
            if (buffer.Length > _serverInfo.MaxPayload)
                throw NatsException.ExceededMaxPayload(_serverInfo.MaxPayload, buffer.LongLength);

            _socket.Send(buffer);
        }

        public void Fail()
        {
            ThrowIfDisposed();

            DoSend($"FAIL{Crlf}");
        }
    }
}