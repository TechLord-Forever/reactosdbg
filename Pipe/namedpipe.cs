﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AbstractPipe
{
    public enum ConnectionMode
    {
        MODE_CLIENT = 0x00000000,
        MODE_SERVER = 0x00000001,
    }

    public class NamedPipe : Pipe, NPipe
    {
        public const int PIPE_SIZE = 1024;

        private PipeStream ioStream; /* stream for io operations */
        private String wCommand; /* buffer of a single command line */
        private List<String> cmdList; /*list of commands pending to be written */
        private bool bClientConn;
        private Thread waitThread;
        private NamedPipeServerStream sStream;
        private NamedPipeClientStream cStream;

        public event EventHandler ClientConnectedEvent;
        public event EventHandler ClientDisconnectedEvent;
        public event PipeReceiveEventHandler PipeReceiveEvent;
        public event PipeErrorEventHandler PipeErrorEvent;

        private static ManualResetEvent newWriteData = new ManualResetEvent(false);

        public bool IsClientConnected
        {
            get { return bClientConn; }
        }

        public NamedPipe()
        {
            cmdList = new List<string>();
        }

        private void signalConnected()
        {
            bClientConn = true;
            if (ClientConnectedEvent != null)
            {
                ClientConnectedEvent(this, EventArgs.Empty);
            }
        }

        private void signalDisconnected()
        {
            bClientConn = false;
            if (ClientDisconnectedEvent != null)
            {
                ClientDisconnectedEvent(this, EventArgs.Empty);
            }
        }

        private void WaitForConnection()
        {
            try
            {
                sStream.WaitForConnection();

                if (sStream.IsConnected)
                {
                    ioStream = sStream;
                    signalConnected();
                }
            }
            catch (IOException)
            {
                /* Pipe got killed externally */
            }
        }

        public void CreateServerPipe(string name)
        {
            /* create a pipe and wait for a client */
            sStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, 
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PIPE_SIZE, PIPE_SIZE);

            waitThread = new Thread(WaitForConnection);
            waitThread.Start();
        }

        private void WaitForServer()
        {
            while (true)
            {
                try
                {
                    cStream.Connect(10);
                }
                catch (TimeoutException)
                {
                    /* Dismiss timeout, will try again */
                }
                catch (Exception)
                {
                    /* Pipe was killed externally */
                    return;
                }
                if (cStream.IsConnected)
                {
                    ioStream = cStream;
                    signalConnected();
                    return;
                }
                Thread.Sleep(500);
            }
        }

        public bool CreateClientPipe(string name)
        {
            /* Try to connect as a client */
            /* (QEMU -serial pipe or VMware in pipe server mode) */
            try
            {
                cStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            }
            catch (Exception)
            {
                /* Pipe couldn't be created */
                return false;
            }
            waitThread = new Thread(WaitForServer);
            waitThread.Start();
            return true;
        }

        public bool CreatePipe(string name, ConnectionMode mode)
        {
            if (name == "" || name == null)
            {
                return false;
            }
            switch (mode)
            {
                case ConnectionMode.MODE_CLIENT:
                    CreateClientPipe(name);
                    return true;

                case ConnectionMode.MODE_SERVER:
                    CreateServerPipe(name);
                    return true;
            }
            return false;
        }

        public void Close()
        {
            if (ioStream != null)
                ioStream.Close();

            bClientConn = false;

            /* Wake up the write thread so it can die */
            newWriteData.Set();

            /* Close connection streams */
            if (waitThread != null)
            {
                if (sStream != null)
                {
                    sStream.Close();
                }
                else if (cStream != null)
                {
                    cStream.Close();
                }
            }
        }

        public void WriteLoop()
        {
            if (!bClientConn)
            {
                if (PipeErrorEvent != null)
                    PipeErrorEvent.Invoke(this, new PipeErrorEventArgs("Client not connected"));
            }
            else
            {
                try
                {
                    while (bClientConn)
                    {
                        if (cmdList.Count > 0)
                        {
                            byte[] wBuf = new byte[cmdList[0].Length];
                            UTF8Encoding.UTF8.GetBytes(cmdList[0], 0, cmdList[0].Length, wBuf, 0);

                            ioStream.Write(wBuf, 0, cmdList[0].Length);

                            /* remove written data from commandlist */
                            cmdList.RemoveAt(0);
                        }
                        else if (cmdList.Count == 0)
                        {
                            /* wait until new data is signaled */
                            newWriteData.Reset();
                            newWriteData.WaitOne();
                        }
                    }
                }
                catch (Exception e)
                {
                    if (PipeErrorEvent != null)
                        PipeErrorEvent.Invoke(this, new PipeErrorEventArgs(e.Message));
                }
            }
        }

        public void ReadLoop()
        {
            byte[] buf = new byte[PIPE_SIZE];
            int read = 0;

            if (!bClientConn)
            {
                if (PipeErrorEvent != null)
                    PipeErrorEvent.Invoke(this, new PipeErrorEventArgs("Client not connected"));
            }
            else
            {
                try
                {
                    while (bClientConn)
                    {
                        read = ioStream.Read(buf, 0, PIPE_SIZE);
                        if (read > 0)
                        {
                            if (PipeReceiveEvent != null)
                                PipeReceiveEvent.Invoke(this, new PipeReceiveEventArgs(UTF8Encoding.UTF8.GetString(buf, 0, read)));
                        }
                        else
                        {
                            /* 
                             * Connecion closed!
                             * We'll hijack this thread and use it to set up our pipe server again.
                             * This thread will terminate once the connection is set up, it does not block.
                            */
                            signalDisconnected();
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (PipeErrorEvent != null)
                        PipeErrorEvent.Invoke(this, new PipeErrorEventArgs(e.Message));
                }
            }
        }

        public bool Write(string str)
        {
            /* only forward a complete line */
            wCommand += str;

            cmdList.Add(wCommand);
            wCommand = null;

            /* wake up the write thread */
            newWriteData.Set();
            return true;
        }
    }
}
