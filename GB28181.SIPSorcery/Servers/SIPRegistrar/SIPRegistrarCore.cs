// ============================================================================
// FileName: RegistrarCore.cs
//
// Description:
// SIP Registrar that strives to be RFC3822 compliant.
//
// Author(s):
// Aaron Clauson
//
// History:
// 21 Jan 2006	Aaron Clauson	Created.
// 22 Nov 2007  Aaron Clauson   Fixed bug where binding refresh was generating a duplicate exception if the uac endpoint changed but the contact did not.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//
// Copyright (c) 2006-2007 Aaron Clauson (aaronc@blueface.ie), Blue Face Ltd, Dublin, Ireland (www.blueface.ie)
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

using GB28181.Logger4Net;
using GB28181;
using GB28181.App;
using GB28181.Sys;
using GB28181.Cache;
using GB28181.Config;
using GB28181.Sys.Model;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using SIPSorcery.Sys;
using SIPSorcery.SIP;

#if UNITTEST
using NUnit.Framework;
#endif

namespace GB28181.Servers
{
    public enum RegisterResultEnum
    {
        Unknown = 0,
        Trying = 1,
        Forbidden = 2,
        Authenticated = 3,
        AuthenticationRequired = 4,
        Failed = 5,
        Error = 6,
        RequestWithNoUser = 7,
        RemoveAllRegistrations = 9,
        DuplicateRequest = 10,
        AuthenticatedFromCache = 11,
        RequestWithNoContact = 12,
        NonRegisterMethod = 13,
        DomainNotServiced = 14,
        IntervalTooBrief = 15,
        SwitchboardPaymentRequired = 16,
    }

    /// <summary>
    /// The registrar core is the class that actually does the work of receiving registration requests and populating and
    /// maintaining the SIP registrations list.
    /// 
    /// From RFC 3261 Chapter "10.2 Constructing the REGISTER Request"
    /// - Request-URI: The Request-URI names the domain of the location service for which the registration is meant.
    /// - The To header field contains the address of record whose registration is to be created, queried, or modified.  
    ///   The To header field and the Request-URI field typically differ, as the former contains a user name. 
    /// 
    /// [ed Therefore:
    /// - The Request-URI inidcates the domain for the registration and should match the domain in the To address of record.
    /// - The To address of record contians the username of the user that is attempting to authenticate the request.]
    /// 
    /// Method of operation:
    ///  - New SIP messages received by the SIP Transport layer and queued before being sent to RegistrarCode for processing. For requests
    ///    or response that match an existing REGISTER transaction the SIP Transport layer will handle the retransmit or drop the request if
    ///    it's already being processed.
    ///  - Any non-REGISTER requests received by the RegistrarCore are responded to with not supported,
    ///  - If a persistence is being used to store registered contacts there will generally be a number of threads running for the
    ///    persistence class. Of those threads there will be one that runs calling the SIPRegistrations.IdentifyDirtyContacts. This call identifies
    ///    expired contacts and initiates the sending of any keep alive and OPTIONs requests.
    /// </summary>
    public class SIPRegistrarCore : ISIPRegistrarCore
    {
        private const int MAX_REGISTER_QUEUE_SIZE = 1000;
        private const int MAX_PROCESS_REGISTER_SLEEP = 10000;
        private const string REGISTRAR_THREAD_NAME_PREFIX = "sipregistrar-core";

        private static ILog logger = AppState.GetLogger("sipregistrar");

        private int m_minimumBindingExpiry = SIPRegistrarBindingsManager.MINIMUM_EXPIRY_SECONDS;

        private ISIPTransport _sipTransport;

        private SIPAuthenticateRequestDelegate _sipRequestAuthenticator_External = SIPRequestAuthenticator.AuthenticateSIPRequest;
        //private SIPAssetPersistor<Customer> CustomerPersistor_External;
        private string m_serverAgent = SIPConstants.SIP_USERAGENT_STRING;
        private bool m_mangleUACContact = false;            // Whether or not to adjust contact URIs that contain private hosts to the value of the bottom via received socket.
        private bool m_strictRealmHandling = false;         // If true the registrar will only accept registration requests for domains it is configured for, otherwise any realm is accepted.
                                                            //private event SIPMonitorLogDelegate m_registrarLogEvent;
                                                            // private SIPUserAgentConfigurationManager m_userAgentConfigs;
        private Queue<SIPNonInviteTransaction> m_registerQueue = new Queue<SIPNonInviteTransaction>();
        private AutoResetEvent m_registerARE = new AutoResetEvent(false);
        //private RSACryptoServiceProvider m_switchbboardRSAProvider; // If available this certificate can be used to sign switchboard tokens.

        public event Action<double, bool> RegisterComplete;     // Event to allow hook into get notifications about the processing time for registrations. The boolean parameter is true of the request contained an authentication header.

        public int BacklogLength => m_registerQueue.Count;

        public bool Stop;

        private bool _needAuthentication = false;
        public bool IsNeedAuthentication => _needAuthentication;

        private SIPAccount _localSipAccount;

        private IMemoCache<Camera> _cameraCache = null;

        /// <summary>
        /// �豸ע�ᵽDMS
        /// </summary>
        public event RPCDmsRegisterDelegate RPCDmsRegisterReceived;
        public event DeviceAlarmSubscribeDelegate DeviceAlarmSubscribe;

        public SIPRegistrarCore(ISIPTransport sipTransport, ISipStorage sipAccountStorage, IMemoCache<Camera> cameraCache, bool mangleUACContact = true, bool strictRealmHandling = true)
        {
            _sipTransport = sipTransport;
            m_mangleUACContact = mangleUACContact;
            m_strictRealmHandling = strictRealmHandling;
            _localSipAccount = sipAccountStorage.GetLocalSipAccout();
            _needAuthentication = _localSipAccount.Authentication;
            _cameraCache = cameraCache;
        }

        public void AddRegisterRequest(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest registerRequest)
        {
            try
            {
                if (registerRequest.Method == SIPMethodsEnum.REGISTER)
                {
                    SIPSorceryPerformanceMonitor.IncrementCounter(SIPSorceryPerformanceMonitor.REGISTRAR_REGISTRATION_REQUESTS_PER_SECOND);

                    int requestedExpiry = GetRequestedExpiry(registerRequest);

                    if (registerRequest.Header.To == null)
                    {
                        logger.Debug("Bad register request, no To header from " + remoteEndPoint + ".");
                        SIPResponse badReqResponse = SIPTransport.GetResponse(registerRequest, SIPResponseStatusCodesEnum.BadRequest, "Missing To header");
                        _sipTransport.SendResponse(badReqResponse);
                    }
                    else if (registerRequest.Header.To.ToURI.User.IsNullOrBlank())
                    {
                        logger.Debug("Bad register request, no To user from " + remoteEndPoint + ".");
                        SIPResponse badReqResponse = SIPTransport.GetResponse(registerRequest, SIPResponseStatusCodesEnum.BadRequest, "Missing username on To header");
                        _sipTransport.SendResponse(badReqResponse);
                    }
                    else if (registerRequest.Header.Contact == null || registerRequest.Header.Contact.Count == 0)
                    {
                        logger.Debug("Bad register request, no Contact header from " + remoteEndPoint + ".");
                        SIPResponse badReqResponse = SIPTransport.GetResponse(registerRequest, SIPResponseStatusCodesEnum.BadRequest, "Missing Contact header");
                        _sipTransport.SendResponse(badReqResponse);
                    }
                    else if (requestedExpiry > 0 && requestedExpiry < m_minimumBindingExpiry)
                    {
                        logger.Debug("Bad register request, no expiry of " + requestedExpiry + " to small from " + remoteEndPoint + ".");
                        SIPResponse tooFrequentResponse = GetErrorResponse(registerRequest, SIPResponseStatusCodesEnum.IntervalTooBrief, null);
                        tooFrequentResponse.Header.MinExpires = m_minimumBindingExpiry;
                        _sipTransport.SendResponse(tooFrequentResponse);
                    }
                    else
                    {
                        if (m_registerQueue.Count < MAX_REGISTER_QUEUE_SIZE)
                        {
                            var registrarTransaction = _sipTransport.CreateNonInviteTransaction(registerRequest, remoteEndPoint, localSIPEndPoint, null);
                            lock (m_registerQueue)
                            {
                                m_registerQueue.Enqueue(registrarTransaction);
                            }
                            logger.Debug("m_registerQueue.Enqueue Counts: " + m_registerQueue.Count);
                            FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.BindingInProgress, "Register queued for " + registerRequest.Header.To.ToURI.ToString() + ".", null));
                        }
                        else
                        {
                            logger.Error("Register queue exceeded max queue size " + MAX_REGISTER_QUEUE_SIZE + ", overloaded response sent.");
                            SIPResponse overloadedResponse = SIPTransport.GetResponse(registerRequest, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Registrar overloaded, please try again shortly");
                            _sipTransport.SendResponse(overloadedResponse);
                        }

                        m_registerARE.Set();
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception AddRegisterRequest (" + remoteEndPoint.ToString() + "). " + excp.Message);
            }
        }

        public void ProcessRegisterRequest()
        {
            logger.Debug("SIPRegistrarCore is running at " + _localSipAccount.MsgProtocol + ":" + _localSipAccount.LocalIP + ":" + _localSipAccount.LocalPort);
            try
            {
                while (!Stop)
                {
                    if (m_registerQueue.Count > 0)
                    {
                        try
                        {
                            SIPNonInviteTransaction registrarTransaction = null;
                            lock (m_registerQueue)
                            {
                                registrarTransaction = m_registerQueue.Dequeue();
                            }

                            if (registrarTransaction != null)
                            {
                                DateTime startTime = DateTime.Now;
                                var result = Register(registrarTransaction);
                                var duration = DateTime.Now.Subtract(startTime);
                                FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegistrarTiming, "register result=" + result.ToString() + ", time=" + duration.TotalMilliseconds + "ms, user=" + registrarTransaction.TransactionRequest.Header.To.ToURI.User + ".", null));
                                RegisterComplete?.Invoke(duration.TotalMilliseconds, registrarTransaction.TransactionRequest.Header.AuthenticationHeader != null);

                                logger.Debug("Camera[" + registrarTransaction.RemoteEndPoint + "] have completed registering GB service.");
                                //CacheDeviceItem(registrarTransaction.TransactionRequest);

                                //device alarm subscribe
                                DeviceAlarmSubscribe?.Invoke(registrarTransaction);
                            }
                        }
                        catch (InvalidOperationException invalidOpExcp)
                        {
                            // This occurs when the queue is empty.
                            logger.Warn("InvalidOperationException ProcessRegisterRequest Register Job. " + invalidOpExcp.Message);
                        }
                        catch (Exception regExcp)
                        {
                            logger.Error("Exception ProcessRegisterRequest Register Job. " + regExcp.Message);
                        }
                    }
                    else
                    {
                        m_registerARE.WaitOne(MAX_PROCESS_REGISTER_SLEEP);
                    }
                }

                logger.Warn("ProcessRegisterRequest thread " + Thread.CurrentThread.Name + " stopping.");
            }
            catch (Exception excp)
            {
                logger.Error("Exception ProcessRegisterRequest (" + Thread.CurrentThread.Name + "). " + excp.Message);
            }
        }

        private int GetRequestedExpiry(SIPRequest registerRequest)
        {
            int contactHeaderExpiry = (registerRequest.Header.Contact != null && registerRequest.Header.Contact.Count > 0) ? registerRequest.Header.Contact[0].Expires : -1;
            return (contactHeaderExpiry == -1) ? registerRequest.Header.Expires : contactHeaderExpiry;
        }

        private void CacheDeviceItem(SIPRequest sipRequest)
        {

            //Add Camera Item Into Cache
            _cameraCache.PlaceIn(sipRequest.URI.Host, new Camera()
            {
                DeviceID = sipRequest.Header.From.FromURI.User,
                IPAddress = sipRequest.Header.Vias.TopViaHeader.Host,
                Port = sipRequest.Header.Vias.TopViaHeader.Port
            });
        }

        private RegisterResultEnum Register(SIPTransaction registerTransaction)
        {
            try
            {
                SIPRequest sipRequest = registerTransaction.TransactionRequest;
                SIPURI registerURI = sipRequest.URI;
                SIPToHeader toHeader = sipRequest.Header.To;
                string toUser = toHeader.ToURI.User;
                //string canonicalDomain = (m_strictRealmHandling) ? GetCanonicalDomain_External(toHeader.ToURI.Host, true) : toHeader.ToURI.Host;
                string canonicalDomain = toHeader.ToURI.Host;
                int requestedExpiry = GetRequestedExpiry(sipRequest);

                if (canonicalDomain == null)
                {
                    FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Warn, "Register request for " + toHeader.ToURI.Host + " rejected as no matching domain found.", null));
                    SIPResponse noDomainResponse = GetErrorResponse(sipRequest, SIPResponseStatusCodesEnum.Forbidden, "Domain not serviced");
                    registerTransaction.SendFinalResponse(noDomainResponse);
                    return RegisterResultEnum.DomainNotServiced;
                }

                //SIPAccount sipAccount = GetSIPAccount_External(s => s.SIPUsername == toUser && s.SIPDomain == canonicalDomain);
                SIPAccount sipAccount = new SIPAccount
                {
                    Id = Guid.NewGuid(),
                    Owner = "admin",
                    SIPUsername = toUser,
                    SIPDomain = canonicalDomain
                };
                //SIPAccount sipAccount = GetSIPAccount_External(s => s.SIPUsername == toUser);
                SIPRequestAuthenticationResult authenticationResult = _sipRequestAuthenticator_External?.Invoke(registerTransaction.LocalSIPEndPoint, registerTransaction.RemoteEndPoint, sipRequest, sipAccount, FireProxyLogEvent);

                if (!_needAuthentication)
                {
                    SIPResponse okRes = GetOkResponse(sipRequest);

                    registerTransaction.SendFinalResponse(okRes);

                    //Add Camera Item Into Cache
                    CacheDeviceItem(sipRequest);
                    RPCDmsRegisterReceived?.Invoke(registerTransaction, _localSipAccount);

                    return RegisterResultEnum.AuthenticationRequired;
                }

                if (!authenticationResult.Authenticated)
                {
                    // 401 Response with a fresh nonce needs to be sent.
                    SIPResponse authReqdResponse = SIPTransport.GetResponse(sipRequest, authenticationResult.ErrorResponse, null);
                    authReqdResponse.Header.AuthenticationHeader = authenticationResult.AuthenticationRequiredHeader;
                    registerTransaction.SendFinalResponse(authReqdResponse);

                    if (authenticationResult.ErrorResponse == SIPResponseStatusCodesEnum.Forbidden)
                    {
                        FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Warn, "Forbidden " + toUser + "@" + canonicalDomain + " does not exist, " + sipRequest.Header.ProxyReceivedFrom + ", " + sipRequest.Header.UserAgent + ".", null));
                        return RegisterResultEnum.Forbidden;
                    }
                    else
                    {
                        FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Registrar, "Authentication required for " + toUser + "@" + canonicalDomain + " from " + sipRequest.Header.ProxyReceivedFrom + ".", toUser));
                        return RegisterResultEnum.AuthenticationRequired;
                    }
                }
                else
                {
                    // Authenticated.
                    //if (!sipRequest.Header.UserAgent.IsNullOrBlank() && !m_switchboarduserAgentPrefix.IsNullOrBlank() && sipRequest.Header.UserAgent.StartsWith(m_switchboarduserAgentPrefix))
                    //{
                    //    // Check that the switchboard user is authorised.
                    //    var customer = CustomerPersistor_External.Get(x => x.CustomerUsername == sipAccount.Owner);
                    //    if (!(customer.ServiceLevel == CustomerServiceLevels.Switchboard.ToString() || customer.ServiceLevel == CustomerServiceLevels.Gold.ToString()))
                    //    {
                    //        FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Warn, "Register request for switchboard from " + toHeader.ToURI.Host + " rejected as not correct service level.", sipAccount.Owner));
                    //        SIPResponse payReqdResponse = GetErrorResponse(sipRequest, SIPResponseStatusCodesEnum.PaymentRequired, "You need to purchase a Switchboard service");
                    //        registerTransaction.SendFinalResponse(payReqdResponse);
                    //        return RegisterResultEnum.SwitchboardPaymentRequired;
                    //    }
                    //}

                    if (sipRequest.Header.Contact == null || sipRequest.Header.Contact.Count == 0)
                    {
                        // No contacts header to update bindings with, return a list of the current bindings.
                        //List<SIPRegistrarBinding> bindings = m_registrarBindingsManager.GetBindings(sipAccount.Id);
                        ////List<SIPContactHeader> contactsList = m_registrarBindingsManager.GetContactHeader(); // registration.GetContactHeader(true, null);
                        //if (bindings != null)
                        //{
                        //    sipRequest.Header.Contact = GetContactHeader(bindings);
                        //}

                        SIPResponse okResponse = GetOkResponse(sipRequest);

                        registerTransaction.SendFinalResponse(okResponse);

                        //Add Camera Item Into Cache
                        CacheDeviceItem(sipRequest);

                        FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegisterSuccess, "Empty registration request successful for " + toUser + "@" + canonicalDomain + " from " + sipRequest.Header.ProxyReceivedFrom + ".", toUser));
                    }
                    else
                    {
                        SIPEndPoint uacRemoteEndPoint = SIPEndPoint.TryParse(sipRequest.Header.ProxyReceivedFrom) ?? registerTransaction.RemoteEndPoint;
                        SIPEndPoint proxySIPEndPoint = SIPEndPoint.TryParse(sipRequest.Header.ProxyReceivedOn);
                        SIPEndPoint registrarEndPoint = registerTransaction.LocalSIPEndPoint;
                        SIPResponseStatusCodesEnum updateResult = SIPResponseStatusCodesEnum.Ok;
                        // string updateMessage = null;
                        DateTime startTime = DateTime.Now;

                        //List<SIPRegistrarBinding> bindingsList = m_registrarBindingsManager.UpdateBindings(
                        //    sipAccount,
                        //    proxySIPEndPoint,
                        //    uacRemoteEndPoint,
                        //    registrarEndPoint,
                        //    //sipRequest.Header.Contact[0].ContactURI.CopyOf(),
                        //    sipRequest.Header.Contact,
                        //    sipRequest.Header.CallId,
                        //    sipRequest.Header.CSeq,
                        //    //sipRequest.Header.Contact[0].Expires,
                        //    sipRequest.Header.Expires,
                        //    sipRequest.Header.UserAgent,
                        //    out updateResult,
                        //    out updateMessage);
                        //int bindingExpiry = GetBindingExpiry(bindingsList, sipRequest.Header.Contact[0].ContactURI.ToString());
                        TimeSpan duration = DateTime.Now.Subtract(startTime);
                        FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegistrarTiming, "Binding update time for " + toUser + "@" + canonicalDomain + " took " + duration.TotalMilliseconds + "ms.", null));

                        if (updateResult == SIPResponseStatusCodesEnum.Ok)
                        {
                            string proxySocketStr = (proxySIPEndPoint != null) ? " (proxy=" + proxySIPEndPoint.ToString() + ")" : null;

                            //  int bindingCount = 1;
                            //foreach (SIPRegistrarBinding binding in bindingsList)
                            //{
                            //    string bindingIndex = (bindingsList.Count == 1) ? String.Empty : " (" + bindingCount + ")";
                            //    //FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegisterSuccess, "Registration successful for " + toUser + "@" + canonicalDomain + " from " + uacRemoteEndPoint + proxySocketStr + ", binding " + binding.ContactSIPURI.ToParameterlessString() + ";expiry=" + binding.Expiry + bindingIndex + ".", toUser));
                            //    FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegisterSuccess, "Registration successful for " + toUser + "@" + canonicalDomain + " from " + uacRemoteEndPoint + ", binding " + binding.ContactSIPURI.ToParameterlessString() + ";expiry=" + binding.Expiry + bindingIndex + ".", toUser));
                            //    //FireProxyLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingUpdate, toUser, uacRemoteEndPoint, sipAccount.Id.ToString()));
                            //    bindingCount++;
                            //}

                            // The standard states that the Ok response should contain the list of current bindings but that breaks some UAs. As a 
                            // compromise the list is returned with the Contact that UAC sent as the first one in the list.
                            //bool contactListSupported = m_userAgentConfigs.GetUserAgentContactListSupport(sipRequest.Header.UserAgent);
                            //if (contactListSupported)
                            //{
                            //    sipRequest.Header.Contact = GetContactHeader(bindingsList);
                            //}
                            //else
                            //{
                            //    // Some user agents can't match the contact header if the expiry is added to it.
                            //    sipRequest.Header.Contact[0].Expires = GetBindingExpiry(bindingsList, sipRequest.Header.Contact[0].ContactURI.ToString()); ;
                            //}

                            SIPResponse okResponse = GetOkResponse(sipRequest);

                            // If a request was made for a switchboard token and a certificate is available to sign the tokens then generate it.
                            //if (sipRequest.Header.SwitchboardTokenRequest > 0 && m_switchbboardRSAProvider != null)
                            //{
                            //    SwitchboardToken token = new SwitchboardToken(sipRequest.Header.SwitchboardTokenRequest, sipAccount.Owner, uacRemoteEndPoint.Address.ToString());

                            //    lock (m_switchbboardRSAProvider)
                            //    {
                            //        token.SignedHash = Convert.ToBase64String(m_switchbboardRSAProvider.SignHash(Crypto.GetSHAHash(token.GetHashString()), null));
                            //    }

                            //    string tokenXML = token.ToXML(true);
                            //    logger.Debug("Switchboard token set for " + sipAccount.Owner + " with expiry of " + token.Expiry + "s.");
                            //    okResponse.Header.SwitchboardToken = Crypto.SymmetricEncrypt(sipAccount.SIPPassword, sipRequest.Header.AuthenticationHeader.SIPDigest.Nonce, tokenXML);
                            //}

                            registerTransaction.SendFinalResponse(okResponse);

                            //Add Camera Item Into Cache
                            CacheDeviceItem(sipRequest);
                        }
                        else
                        {
                            // The binding update failed even though the REGISTER request was authorised. This is probably due to a 
                            // temporary problem connecting to the bindings data store. Send Ok but set the binding expiry to the minimum so
                            // that the UA will try again as soon as possible.
                            FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Error, "Registration request successful but binding update failed for " + toUser + "@" + canonicalDomain + " from " + registerTransaction.RemoteEndPoint + ".", toUser));
                            sipRequest.Header.Contact[0].Expires = m_minimumBindingExpiry;
                            SIPResponse okResponse = GetOkResponse(sipRequest);
                            registerTransaction.SendFinalResponse(okResponse);

                        }
                    }

                    return RegisterResultEnum.Authenticated;
                }
            }
            catch (Exception excp)
            {
                string regErrorMessage = "Exception registrarcore registering. " + excp.Message + "\r\n" + registerTransaction.TransactionRequest.ToString();
                logger.Error(regErrorMessage);
                FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.Error, regErrorMessage, null));

                try
                {
                    SIPResponse errorResponse = GetErrorResponse(registerTransaction.TransactionRequest, SIPResponseStatusCodesEnum.InternalServerError, null);
                    registerTransaction.SendFinalResponse(errorResponse);
                }
                catch { }

                return RegisterResultEnum.Error;
            }
        }

        private int GetBindingExpiry(List<SIPRegistrarBinding> bindings, string bindingURI)
        {
            if (bindings != null || bindings.Count > 0)
            {
                var target = bindings.FirstOrDefault(item => item.ContactURI == bindingURI);

                if (target != null)
                {
                    return target.Expiry;
                }
            }
            return -1;
        }

        /// <summary>
        /// Gets a SIP contact header for this address-of-record based on the bindings list.
        /// </summary>
        /// <returns></returns>
        private List<SIPContactHeader> GetContactHeader(List<SIPRegistrarBinding> bindings)
        {
            if (bindings != null && bindings.Count > 0)
            {
                List<SIPContactHeader> contactHeaderList = new List<SIPContactHeader>();

                foreach (SIPRegistrarBinding binding in bindings)
                {
                    SIPContactHeader bindingContact = new SIPContactHeader(null, binding.ContactSIPURI)
                    {
                        Expires = Convert.ToInt32(binding.ExpiryTime.Subtract(DateTime.UtcNow).TotalSeconds % Int32.MaxValue)
                    };
                    contactHeaderList.Add(bindingContact);
                }

                return contactHeaderList;
            }
            else
            {
                return null;
            }
        }

        private SIPResponse GetOkResponse(SIPRequest sipRequest)
        {
            try
            {
                SIPResponse okResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                SIPHeader requestHeader = sipRequest.Header;
                okResponse.Header = new SIPHeader(requestHeader.Contact, requestHeader.From, requestHeader.To, requestHeader.CSeq, requestHeader.CallId);

                // RFC3261 has a To Tag on the example in section "24.1 Registration".
                if (okResponse.Header.To.ToTag == null || okResponse.Header.To.ToTag.Trim().Length == 0)
                {
                    okResponse.Header.To.ToTag = CallProperties.CreateNewTag();
                }

                okResponse.Header.CSeqMethod = requestHeader.CSeqMethod;
                okResponse.Header.Vias = requestHeader.Vias;
                //okResponse.Header.Server = m_serverAgent;
                okResponse.Header.UserAgent = m_serverAgent;
                okResponse.Header.MaxForwards = Int32.MinValue;
                okResponse.Header.SetDateHeader();

                return okResponse;
            }
            catch (Exception excp)
            {
                logger.Error("Exception GetOkResponse. " + excp.Message);
                throw excp;
            }
        }

        private SIPResponse GetAuthReqdResponse(SIPRequest sipRequest, string nonce, string realm)
        {
            try
            {
                SIPResponse authReqdResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Unauthorised, null);
                SIPAuthenticationHeader authHeader = new SIPAuthenticationHeader(SIPAuthorisationHeadersEnum.WWWAuthenticate, realm, nonce);
                SIPHeader requestHeader = sipRequest.Header;
                SIPHeader unauthHeader = new SIPHeader(requestHeader.Contact, requestHeader.From, requestHeader.To, requestHeader.CSeq, requestHeader.CallId);

                if (unauthHeader.To.ToTag == null || unauthHeader.To.ToTag.Trim().Length == 0)
                {
                    unauthHeader.To.ToTag = CallProperties.CreateNewTag();
                }

                unauthHeader.CSeqMethod = requestHeader.CSeqMethod;
                unauthHeader.Vias = requestHeader.Vias;
                unauthHeader.AuthenticationHeader = authHeader;
                //unauthHeader.Server = m_serverAgent;
                unauthHeader.UserAgent = m_serverAgent;
                unauthHeader.MaxForwards = Int32.MinValue;

                authReqdResponse.Header = unauthHeader;

                return authReqdResponse;
            }
            catch (Exception excp)
            {
                logger.Error("Exception GetAuthReqdResponse. " + excp.Message);
                throw excp;
            }
        }

        private SIPResponse GetErrorResponse(SIPRequest sipRequest, SIPResponseStatusCodesEnum errorResponseCode, string errorMessage)
        {
            try
            {
                SIPResponse errorResponse = SIPTransport.GetResponse(sipRequest, errorResponseCode, null);
                if (errorMessage != null)
                {
                    errorResponse.ReasonPhrase = errorMessage;
                }

                SIPHeader requestHeader = sipRequest.Header;
                SIPHeader errorHeader = new SIPHeader(requestHeader.Contact, requestHeader.From, requestHeader.To, requestHeader.CSeq, requestHeader.CallId);

                if (errorHeader.To.ToTag == null || errorHeader.To.ToTag.Trim().Length == 0)
                {
                    errorHeader.To.ToTag = CallProperties.CreateNewTag();
                }

                errorHeader.CSeqMethod = requestHeader.CSeqMethod;
                errorHeader.Vias = requestHeader.Vias;
                //errorHeader.Server = m_serverAgent;
                errorHeader.UserAgent = m_serverAgent;
                errorHeader.MaxForwards = Int32.MinValue;

                errorResponse.Header = errorHeader;

                return errorResponse;
            }
            catch (Exception excp)
            {
                logger.Error("Exception GetErrorResponse. " + excp.Message);
                throw excp;
            }
        }

        private void FireProxyLogEvent(SIPMonitorEvent monitorEvent)
        {
            //if (m_registrarLogEvent != null)
            //{
            //    try
            //    {
            //        m_registrarLogEvent(monitorEvent);
            //    }
            //    catch (Exception excp)
            //    {
            //        logger.Error("Exception FireProxyLogEvent RegistrarCore. " + excp.Message);
            //    }
            //}
        }
    }
}
