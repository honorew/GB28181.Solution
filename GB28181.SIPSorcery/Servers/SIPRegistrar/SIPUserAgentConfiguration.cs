﻿// ============================================================================
// FileName: SIPUserAgentConfiguration.cs
//
// Description:
// This class describes the settings for SIP user agents for use by the SIP Registrar. This allows
// the registrar to apply different behaviour for different user agents. Some user agents only work
// with a specific expiry time, others will only recognise a response if the Contact header is returned
// exactly as sent rather than as a list of all current contacts as the standard mandates.
//
// Author(s):
// Aaron Clauson
//
// History:
// 07 Sep 2008	Aaron Clauson	Created.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//


using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using GB28181.Sys;
using GB28181.Logger4Net;

#if UNITTEST
using NUnit.Framework;
#endif

namespace GB28181.Servers {

    public class SIPUserAgentConfiguration {

        public const int DEFAULT_MAX_EXPIRY_SECONDS = 3600;

        public int MaxAllowedExpiryTime = 3600;     // Overrules the default max expiry time the Registrar is using and allows specific user agents to have expirys up to this value.
        public bool ContactListSupported = true;    // If false means the user agent wants only the Contact header it supplied returned in the Ok response.
        public string UserAgentRegex = null;        // The regular expression string being used to match the user agent.

        public SIPUserAgentConfiguration(int maxExpiry, bool listSupported, string userAgentRegex) {
            MaxAllowedExpiryTime = maxExpiry;
            ContactListSupported = listSupported;
            UserAgentRegex = userAgentRegex;
        }
    }

    public class SIPUserAgentConfigurationManager {

        private static ILog logger = AppState.logger;

        private int m_defaultMaxExpiry = SIPUserAgentConfiguration.DEFAULT_MAX_EXPIRY_SECONDS;
        private bool m_defaultContactListSupported = true;

        private Dictionary<string, SIPUserAgentConfiguration> m_userAgentConfigs;

        public string DefaultUserAgent
        {
            get
            {
                foreach (var item in m_userAgentConfigs)
                {
                    if (item.Value.ContactListSupported)
                    {
                        return item.Value.UserAgentRegex;
                    }
                }
                return null;
            }
        }

        public SIPUserAgentConfigurationManager(XmlNode userAgentConfigNode) {
            m_userAgentConfigs = ParseSIPUserAgentConfigurations(userAgentConfigNode);
        }

        private Dictionary<string, SIPUserAgentConfiguration> ParseSIPUserAgentConfigurations(XmlNode userAgentConifgNode) {
            try {
                Dictionary<string, SIPUserAgentConfiguration> userAgentConfigs = new Dictionary<string, SIPUserAgentConfiguration>();

                if (userAgentConifgNode != null && userAgentConifgNode.ChildNodes.Count != 0) {
                    foreach (XmlNode userAgentNode in userAgentConifgNode.SelectNodes("useragent")) {
                        //if (userAgentNode.InnerText != null && userAgentNode.InnerText.Trim().Length > 0) {
                            int expiry = Convert.ToInt32(userAgentNode.Attributes.GetNamedItem("expiry").Value);
                            bool contactListSupported = (userAgentNode.Attributes.GetNamedItem("contactlists") != null) ? Convert.ToBoolean(userAgentNode.Attributes.GetNamedItem("contactlists").Value) : true;
                            string userAgent = userAgentNode.Attributes.GetNamedItem("agent").Value;
                            SIPUserAgentConfiguration userAgentConfig = new SIPUserAgentConfiguration(expiry, contactListSupported, userAgent);

                            if (userAgentConfig.UserAgentRegex != null && userAgentConfig.UserAgentRegex.Trim().Length > 0 && !userAgentConfigs.ContainsKey(userAgentConfig.UserAgentRegex)) {
                                logger.Debug("Added useragent config, useragent=" + userAgentConfig.UserAgentRegex + ", expiry=" + userAgentConfig.MaxAllowedExpiryTime + "s, contact lists=" + userAgentConfig.ContactListSupported + ".");
                                userAgentConfigs.Add(userAgentConfig.UserAgentRegex, userAgentConfig);
                            }
                        //}
                    }
                }

                return userAgentConfigs;
            }
            catch (Exception excp) {
                logger.Error("Exception ParseSIPUserAgentConfigurations. " + excp.Message);
                return null;
            }
        }

        /// <summary>
        /// Makes a decision on what the maximum allowed expiry is for a REGISTER request. Allows different expiry values to be accepted from different user agents.
        /// This is useful as some user agents ignore the expiry value set by the server and setting a higher value for that user agent can stop the registrar
        /// expiring it.
        /// </summary>
        /// <param name="userAgent">The useragent to get the maximum expiry for.</param>
        /// <returns>The maximum expiry value that will be accepted.</returns>
        private SIPUserAgentConfiguration GetUserAgentConfig(string userAgent) {
            
            SIPUserAgentConfiguration matchingUAConfig = null;

            try {
                if (m_userAgentConfigs != null && m_userAgentConfigs.Count > 0) {
                    if (userAgent != null && userAgent.Trim().Length > 0) {
                        foreach (string userAgentPattern in m_userAgentConfigs.Keys) {
                            if (Regex.Match(userAgent, userAgentPattern, RegexOptions.IgnoreCase).Success) {
                                matchingUAConfig = m_userAgentConfigs[userAgentPattern];
                                break;
                            }
                        }
                    }
                }

                return matchingUAConfig;
            }
            catch (Exception excp) {
                logger.Error("Exception GetUserAgentConfig. " + excp);
                return null;
            }
        }

        public int GetMaxAllowedExpiry(string userAgent) {
            SIPUserAgentConfiguration matchingConfig = GetUserAgentConfig(userAgent);
            return (matchingConfig != null) ? matchingConfig.MaxAllowedExpiryTime : m_defaultMaxExpiry;
        }

        public bool GetUserAgentContactListSupport(string userAgent) {
            SIPUserAgentConfiguration matchingConfig = GetUserAgentConfig(userAgent);
            return (matchingConfig != null) ? matchingConfig.ContactListSupported : m_defaultContactListSupported;
        }

        #region Unit testing.

        #if UNITTEST

        [TestFixture]
        public class SIPUserAgentConfigurationManagerUnitTest {

            [TestFixtureSetUp]
            public void Init() { }

            [TestFixtureTearDown]
            public void Dispose() { }

            [Test]
            public void SampleTest() {
                Console.WriteLine("--> " + System.Reflection.MethodBase.GetCurrentMethod().Name);

                Assert.IsTrue(true, "True was false.");

                Console.WriteLine("---------------------------------");
            }

            [Test]
            public void GetExpiryUnitTest() {
                Console.WriteLine("--> " + System.Reflection.MethodBase.GetCurrentMethod().Name);

                string registrarExpiryNode =
                    "<useragentconfigs>" +
                    "  <useragent expiry='3600' contactlists='false'>fring</useragent>" +
                    "  <useragent expiry='113'>*</useragent>" +
                    " </useragentconfigs>";
                XmlDocument regExpiryDom = new XmlDocument();
                regExpiryDom.LoadXml(registrarExpiryNode);

                SIPUserAgentConfigurationManager uaConfigManager = new SIPUserAgentConfigurationManager(regExpiryDom.DocumentElement);
                SIPUserAgentConfiguration matchingUA = uaConfigManager.GetUserAgentConfig("fring");

                Assert.IsNotNull(matchingUA, "A matching user agent entry was not found.");
                Assert.IsTrue(matchingUA.MaxAllowedExpiryTime == 3600, "The expiry value for the fring ua was incorrect.");
                Assert.IsTrue(!matchingUA.ContactListSupported, "The contact lists supported for fring ua was incorrect.");

                Console.WriteLine("---------------------------------");
            }

            [Test]
            public void GetCiscoExpiryUnitTest() {
                Console.WriteLine("--> " + System.Reflection.MethodBase.GetCurrentMethod().Name);

                string registrarExpiryNode =
                    "<useragentconfigs>" +
                    "  <useragent expiry='3600'>fring</useragent>" +
                    "  <useragent expiry='300'>Cisco-CP7960G/8.0</useragent>" +
                    "  <useragent expiry='113'>*</useragent>" +
                    " </useragentconfigs>";
                XmlDocument regExpiryDom = new XmlDocument();
                regExpiryDom.LoadXml(registrarExpiryNode);

                SIPUserAgentConfigurationManager uaConfigManager = new SIPUserAgentConfigurationManager(regExpiryDom.DocumentElement);
                SIPUserAgentConfiguration matchingUA = uaConfigManager.GetUserAgentConfig("Cisco-CP7960G/8.0");

                Assert.IsNotNull(matchingUA, "A matching user agent entry was not found.");
                Assert.IsTrue(matchingUA.MaxAllowedExpiryTime == 300, "The expiry value for the Cisco ua was incorrect.");

                Console.WriteLine("---------------------------------");
            }
        }

        #endif

        #endregion
    }
}
