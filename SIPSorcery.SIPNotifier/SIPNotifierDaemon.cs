﻿// ============================================================================
// FileName: SIPNotifierDaemon.cs
//
// Description:
// A daemon to configure and start a SIP Notifier Server.
//
// Author(s):
// Aaron Clauson
//
// History:
// 22 Feb 2010	Aaron Clauson	Created.
//
// License: 
// This software is licensed under the BSD License http://www.opensource.org/licenses/bsd-license.php
//
// Copyright (c) 2010 Aaron Clauson (aaron@sipsorcery.com), SIPSorcery Ltd, London, UK (www.blueface.ie)
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that 
// the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. 
// Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following 
// disclaimer in the documentation and/or other materials provided with the distribution. Neither the name of Blue Face Ltd. 
// nor the names of its contributors may be used to endorse or promote products derived from this software without specific 
// prior written permission. 
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, 
// BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
// IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, 
// OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
// OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, 
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
// POSSIBILITY OF SUCH DAMAGE.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using SIPSorcery.Persistence;
using SIPSorcery.CRM;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Servers;
using SIPSorcery.Sys;
using SIPSorcery.Web.Services;
using log4net;

namespace SIPSorcery.SIPNotifier
{
    public class SIPNotifierDaemon
    {
        private ILog logger = AppState.logger;

        private XmlNode m_sipNotifierSocketsNode = SIPNotifierState.SIPNotifierSocketsNode;
        private int m_monitorLoopbackPort = SIPNotifierState.MonitorLoopbackPort;
        private SIPEndPoint m_outboundProxy = SIPNotifierState.OutboundProxy;
        private string m_udpNotificationReceiverSocket = SIPNotifierState.MonitorEventReceiveSocket;

        private SIPTransport m_sipTransport;
        private SIPMonitorEventWriter m_monitorEventWriter;
        private NotifierCore m_notifierCore;
        private SIPNotifyManager m_notifyManager;

        private GetCanonicalDomainDelegate GetCanonicalDomain_External;
        private SIPAssetGetDelegate<Customer> GetCustomer_External;
        private SIPAssetGetListDelegate<SIPDialogueAsset> GetDialogues_External;
        private SIPAssetGetByIdDelegate<SIPDialogueAsset> GetDialogue_External;
        private SIPAssetGetListDelegate<SIPRegistrarBinding> GetSIPRegistrarBindings_External;
        private SIPAssetCountDelegate<SIPRegistrarBinding> GetBindingsCount_External;
        private SIPAuthenticateRequestDelegate SIPAuthenticateRequest_External;
        private SIPAssetPersistor<SIPAccountAsset> m_sipAssetPersistor;
        private ISIPMonitorPublisher m_publisher;

        public SIPNotifierDaemon(
            SIPAssetGetDelegate<Customer> getCustomer,
            SIPAssetGetListDelegate<SIPDialogueAsset> getDialogues,
            SIPAssetGetByIdDelegate<SIPDialogueAsset> getDialogue,
            GetCanonicalDomainDelegate getDomain,
            SIPAssetPersistor<SIPAccountAsset> sipAsssetPersistor,
            SIPAssetGetListDelegate<SIPRegistrarBinding> getSIPRegistrarBindings,
            SIPAssetGetListDelegate<SIPAccountAsset> getSIPAccounts,
            SIPAssetCountDelegate<SIPRegistrarBinding> getBindingsCount,
            SIPAuthenticateRequestDelegate sipRequestAuthenticator,
            ISIPMonitorPublisher publisher)
        {
            GetCustomer_External = getCustomer;
            GetDialogues_External = getDialogues;
            GetDialogue_External = getDialogue;
            GetCanonicalDomain_External = getDomain;
            m_sipAssetPersistor = sipAsssetPersistor;
            GetSIPRegistrarBindings_External = getSIPRegistrarBindings;
            GetBindingsCount_External = getBindingsCount;
            SIPAuthenticateRequest_External = sipRequestAuthenticator;
            m_publisher = publisher ?? new SIPMonitorUDPSink(m_udpNotificationReceiverSocket);
        }

        public void Start()
        {
            try
            {
                logger.Debug("SIP Notifier Daemon starting...");

                // Pre-flight checks.
                if (m_sipNotifierSocketsNode == null || m_sipNotifierSocketsNode.ChildNodes.Count == 0)
                {
                    throw new ApplicationException("The SIP Notifier cannot start without at least one socket specified to listen on, please check config file.");
                }

                // Send events from this process to the monitoring socket.
                if (m_monitorLoopbackPort != 0)
                {
                    // Events will be sent by the monitor channel to the loopback interface and this port.
                    m_monitorEventWriter = new SIPMonitorEventWriter(m_monitorLoopbackPort);
                    logger.Debug("Monitor channel initialised for 127.0.0.1:" + m_monitorLoopbackPort + ".");
                }

                // Configure the SIP transport layer.
                m_sipTransport = new SIPTransport(SIPDNSManager.ResolveSIPService, new SIPTransactionEngine(), false);
                List<SIPChannel> sipChannels = SIPTransportConfig.ParseSIPChannelsNode(m_sipNotifierSocketsNode);
                m_sipTransport.AddSIPChannel(sipChannels);

                m_notifierCore = new NotifierCore(
                    FireSIPMonitorEvent, 
                    m_sipTransport, 
                    GetCustomer_External, 
                    GetDialogues_External,
                    GetDialogue_External, 
                    GetCanonicalDomain_External, 
                    m_sipAssetPersistor,
                    GetBindingsCount_External,
                    SIPAuthenticateRequest_External, 
                    m_outboundProxy, 
                    m_publisher);

                m_notifyManager = new SIPNotifyManager(
                    m_sipTransport,
                    m_outboundProxy,
                    FireSIPMonitorEvent,
                    m_sipAssetPersistor.Get,
                    GetSIPRegistrarBindings_External,
                    GetCanonicalDomain_External);

                m_sipTransport.SIPTransportRequestReceived += GotRequest;

                logger.Debug("SIP Notifier Daemon successfully started.");
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPNotifierDaemon Start. " + excp.Message);
            }
        }

        public void Stop()
        {
            try
            {
                logger.Debug("SIP Notifier daemon stopping...");

                if (m_notifierCore != null)
                {
                    m_notifierCore.Stop();
                }

                if (m_notifyManager != null)
                {
                    m_notifyManager.Stop();
                }

                logger.Debug("Shutting down SIP Transport.");
                m_sipTransport.Shutdown();

                logger.Debug("SIP Notifier daemon stopped.");
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPNotifierDaemon Stop. " + excp.Message);
            }
        }

        public void GotRequest(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            try
            {
                if (sipRequest.Method == SIPMethodsEnum.NOTIFY)
                {
                    if (sipRequest.Header.UnknownHeaders.Exists(s => s.Contains("Event: keep-alive")))
                    {
                        // If this is a NOTIFY request that's being sent for NAT keep-alive purposes repond with Ok.
                        SIPResponse okResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                        m_sipTransport.SendResponse(okResponse);
                    }
                    if (GetCanonicalDomain_External(sipRequest.URI.Host, true) != null && !sipRequest.URI.User.IsNullOrBlank())
                    {
                        m_notifyManager.QueueNotification(sipRequest);
                    }
                    else
                    {
                        // Send Not Serviced response to server.
                        SIPResponse notServicedResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.NotFound, "Domain not serviced");
                        m_sipTransport.SendResponse(notServicedResponse);
                    }
                }
                else if (sipRequest.Method == SIPMethodsEnum.SUBSCRIBE)
                {
                    m_notifierCore.AddSubscribeRequest(localSIPEndPoint, remoteEndPoint, sipRequest);
                }
                else
                {
                    SIPResponse methodNotSupportedResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.MethodNotAllowed, null);
                    m_sipTransport.SendResponse(methodNotSupportedResponse);
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SIPNotifierDaemon GotRequest. " + excp.Message);
            }
        }

        private void FireSIPMonitorEvent(SIPMonitorEvent sipMonitorEvent)
        {
            try
            {
                if (sipMonitorEvent != null)
                {
                    if (sipMonitorEvent is SIPMonitorConsoleEvent)
                    {
                        SIPMonitorConsoleEvent consoleEvent = sipMonitorEvent as SIPMonitorConsoleEvent;

                        if (consoleEvent.EventType != SIPMonitorEventTypesEnum.NATKeepAlive)
                        {
                            logger.Debug("no: " + sipMonitorEvent.Message);
                        }
                    }

                    if (m_monitorEventWriter != null)
                    {
                        m_monitorEventWriter.Send(sipMonitorEvent);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception FireSIPMonitorEvent. " + excp.Message);
            }
        }
    }
}
