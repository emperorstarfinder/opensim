/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Threading;
using System.Reflection;
using log4net;
using OpenSim.Framework.Monitoring;
using Amib.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace OpenSim.Framework.Servers.HttpServer
{
    public class PollServiceRequestManager
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private BlockingCollection<PollServiceHttpRequest> m_requests = new BlockingCollection<PollServiceHttpRequest>();
        private ConcurrentQueue<PollServiceHttpRequest> m_retryRequests = new ConcurrentQueue<PollServiceHttpRequest>();

        private uint m_WorkerThreadCount = 0;
        private Thread[] m_workerThreads;
        private Thread m_retrysThread;

        private bool m_running = false;

        public PollServiceRequestManager(
            bool performResponsesAsync, uint pWorkerThreadCount, int pTimeout)
        {
            m_WorkerThreadCount = pWorkerThreadCount;
            m_workerThreads = new Thread[m_WorkerThreadCount];
        }

        public void Start()
        {
            if(m_running)
                return;
            m_running = true;
            //startup worker threads
            for (uint i = 0; i < m_WorkerThreadCount; i++)
            {
                m_workerThreads[i]
                    = WorkManager.StartThread(
                        PoolWorkerJob,
                        string.Format("PollServiceWorkerThread {0}", i),
                        ThreadPriority.Normal,
                        true,
                        false,
                        null,
                        int.MaxValue);
            }

            m_retrysThread = WorkManager.StartThread(
                this.CheckRetries,
                string.Format("PollServiceWatcherThread"),
                ThreadPriority.Normal,
                true,
                true,
                null,
                1000 * 60 * 10);
        }

        private void ReQueueEvent(PollServiceHttpRequest req)
        {
            if (m_running)
                m_retryRequests.Enqueue(req);
        }

        public void Enqueue(PollServiceHttpRequest req)
        {
            if(m_running)
                m_requests.Add(req);
        }

        private void CheckRetries()
        {
            PollServiceHttpRequest preq;
            while (m_running)
            {
                Thread.Sleep(100);
                Watchdog.UpdateThread();
                while (m_running && m_retryRequests.TryDequeue(out preq))
                    m_requests.Add(preq);
            }
        }

        public void Stop()
        {
            if(!m_running)
                return;

            m_running = false;

            Thread.Sleep(100); // let the world move

            foreach (Thread t in m_workerThreads)
                Watchdog.AbortThread(t.ManagedThreadId);

            PollServiceHttpRequest req;
            try
            {
                while (m_retryRequests.TryDequeue(out req))
                    req.DoHTTPstop();
            }
            catch
            {
            }

            try
            {
                while(m_requests.TryTake(out req, 0))
                    req.DoHTTPstop();
            }
            catch
            {
            }
            m_requests.Dispose();
        }

        // work threads

        private void PoolWorkerJob()
        {
            PollServiceHttpRequest req;
            while (m_running)
            {
                try
                {
                    req = null;
                    if (!m_requests.TryTake(out req, 4500) || req == null)
                    {
                        Watchdog.UpdateThread();
                        continue;
                    }

                    Watchdog.UpdateThread();

                    if (!req.Request.Context.CanSend())
                    {
                        req.PollServiceArgs.Drop(req.RequestID, req.PollServiceArgs.Id);
                        continue;
                    }

                    if (req.Request.Context.IsSending())
                    {
                        ReQueueEvent(req);
                        continue;
                    }

                    if (req.PollServiceArgs.HasEvents(req.RequestID, req.PollServiceArgs.Id))
                    {
                        try
                        {
                            Hashtable responsedata = req.PollServiceArgs.GetEvents(req.RequestID, req.PollServiceArgs.Id);
                            req.DoHTTPGruntWork(responsedata);
                        }
                        catch { }
                    }
                    else
                    {
                        if ((Environment.TickCount - req.RequestTime) > req.PollServiceArgs.TimeOutms)
                        {
                            try
                            {
                                req.DoHTTPGruntWork(req.PollServiceArgs.NoEvents(req.RequestID, req.PollServiceArgs.Id));
                            }
                            catch { }
                        }
                        else
                        {
                            ReQueueEvent(req);
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    Thread.ResetAbort();
                    // Shouldn't set this to 'false', the normal shutdown should cause things to exit
                    // but robust is still not normal neither is mono
                    m_running = false;
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("Exception in poll service thread: " + e.ToString());
                }
            }
        }
    }
}
