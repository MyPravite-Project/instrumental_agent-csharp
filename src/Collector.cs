﻿//  Copyright 2014 Bloomerang
//  Copyright 2016 Expected Behavior, LLC
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Sockets;
using System.Threading;
using Common.Logging;

namespace Instrumental
{
  class Collector
  {
    private const int Backoff = 2;
    private const int MaxReconnectDelay = 15;
    private readonly string _apiKey;

    private const int MaxBuffer = 5000;
    private readonly BlockingCollection<String> _messages = new BlockingCollection<String>(MaxBuffer);
    private string _currentCommand;
    private BackgroundWorker _worker;
    private bool _queueFullWarned;
    private static readonly ILog _log = LogManager.GetCurrentClassLogger();

    private static readonly string InstrumentalAddress = "collector.instrumentalapp.com";
    private static readonly int InstrumentalPort = 8000;
    private static readonly byte[] InstrumentalOk = {111, 107, 10};

    public int MessageCount
    {
      get
        {
          return _messages.Count;
        }
    }

    public Collector(String apiKey)
    {
      _apiKey = apiKey;
      StartBackgroundWorker();
    }

    public void SendMessage(String message)
    {
      //this is a good place to test message for only safe characters (or at least, no \r or \n)
      if(_messages.TryAdd(message))
        {
          if(_queueFullWarned)
            {
              _queueFullWarned = false;
              _log.Info("Queue no longer full, processing messages");
            }
        }
      else
        {
          if(!_queueFullWarned)
            {
              _queueFullWarned = true;
              _log.Warn("Queue full.  Dropping messages until there is room");
            }
        }
    }

    private void StartBackgroundWorker()
    {
      _worker = new BackgroundWorker();
      _worker.DoWork += WorkerOnDoWork;
      _worker.RunWorkerAsync();
    }

    private void WorkerOnDoWork(object sender, DoWorkEventArgs doWorkEventArgs)
    {
      var failures = 0;
      Socket socket = null;

      // This is not really the worker loop, it is just very careful connect/authenticate
      // If you want the worker loop, look at SendQueuedMessages
      while (!_worker.CancellationPending)
        {
          try
            {
              socket = Connect();
              Authenticate(socket);
              SendQueuedMessages(socket);
              socket.Shutdown(SocketShutdown.Both);
              socket.Close();
              failures = 0;
            }
          catch (Exception e)
            {
              _log.Error("An exception occurred", e);
              if (socket != null)
                {
                  try {
                    socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                  } catch {}
                  socket = null;
                }
              var delay = (int) Math.Min(MaxReconnectDelay, Math.Pow(failures++, Backoff));
              _log.ErrorFormat("Disconnected. {0} failures in a row. Reconnect in {1} seconds.", failures, delay);
              Thread.Sleep(delay*1000);
            }
        }
    }

    private void SendQueuedMessages(Socket socket)
    {
      while (!_worker.CancellationPending)
        {
          // only pop if the last _currentCommand did not send
          if (_currentCommand == null)
            _currentCommand = _messages.Take();

          if (IsSocketDisconnected(socket))
            throw new Exception("Disconnected");

          _log.DebugFormat("Sending: {0}", _currentCommand);
          var data = System.Text.Encoding.ASCII.GetBytes(_currentCommand + "\n");

          socket.Send(data);
          _currentCommand = null;
        }
    }

    private void Authenticate(Socket socket)
    {
      var data = System.Text.Encoding.ASCII.GetBytes("hello version dotnet/0.2.0\n");
      socket.Send(data);
      if(!ReceiveOk(socket)) throw new Exception("Instrumental Authentication Failed");
      data = System.Text.Encoding.ASCII.GetBytes($"authenticate {_apiKey}\n");
      socket.Send(data);
      if(!ReceiveOk(socket)) throw new Exception("Instrumental AuthenticationFailed");
    }

    private bool ReceiveOk(Socket socket)
    {
      byte[] buffer = new byte[3];
      socket.Receive(buffer);
      // I'm not including all of LINQ for this shit
      // return InstrumentalOk.SequenceEqual(buffer);
      return InstrumentalOk[0] == buffer[0] && InstrumentalOk[1] == buffer[1] && InstrumentalOk[2] == buffer[2];
    }

    private static Socket Connect()
    {
      var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
      _log.Info("Connecting to collector.");
      socket.Connect(InstrumentalAddress, InstrumentalPort);
      _log.Info("Connected to collector.");
      return socket;
    }

    private static bool IsSocketDisconnected (Socket socket)
    {
      // Is there any data available?
      byte[] buffer = null;
      while (socket.Poll(1, SelectMode.SelectRead))
        {
          // If no data is available then socket disconnected
          if (socket.Available == 0)
            return true;

          // Clear socket data; we don't care what InstrumentApp sends
          buffer = buffer ?? new byte[Math.Min(1024, socket.Available)];
          do
            {
              socket.Receive(buffer);
            }
          while (socket.Available != 0);
        }
      return false;
    }
  }
}
