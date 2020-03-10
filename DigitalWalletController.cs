using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using Common.Logging;
using Microsoft.Web.Services2.Security.X509;
using Newtonsoft.Json;
using Psi.Business.Utilities;
using Psi.Data.Models.ClientConfigurationModels;
using Psi.Data.Models.Domain;
using Psi.Data.Models.Domain.CardValet;
using RestSharp;
using RestSharp.Authenticators;

using LogManager = NLog.LogManager;
using X509CertificateCollection = System.Security.Cryptography.X509Certificates.X509CertificateCollection;

namespace ServiceHost.Container.Controller
{
    [RoutePrefix("api/digital-wallet")]
    public class DigitalWalletController : ApiController
    {
        private readonly ICryptoProvider _cryptoProvider;

        private readonly ILog _logger;

        private readonly string _securityKey;

        private readonly Settings _settings;

        public DigitalWalletController(ISettingsBase settingsBase, ICryptoProvider cryptoProvider, ILog logger)
        {
            _settings = new Settings(settingsBase);
            _cryptoProvider = cryptoProvider;
            _logger = logger;
            _securityKey = _settings.MobileConfiguration.DigitalWallet.EncryptionSecurityKey;
        }

        /// <summary>
        ///     Get the certificate from the store as provided by FIS
        /// </summary>
        public X509Certificate GetCertificate(string certificateName)
        {
            X509Certificate cert = null;

            // First check local machine store
            var certificateStore = X509CertificateStore.LocalMachineStore(X509CertificateStore.MyStore);
            certificateStore.OpenRead();
            foreach (X509Certificate certificate in certificateStore.Certificates)
            {
                if (certificate.SimpleDisplayName.EqualsIgnoreCase(certificateName))
                {
                    cert = certificate;
                    break;
                }
            }

            // If not found, check root
            if (cert == null)
            {
                certificateStore = X509CertificateStore.LocalMachineStore(X509CertificateStore.RootStore);
                certificateStore.OpenRead();
                foreach (X509Certificate certificate in certificateStore.Certificates)
                {
                    if (certificate.SimpleDisplayName.EqualsIgnoreCase(certificateName))
                    {
                        cert = certificate;
                        break;
                    }
                }
            }

            certificateStore.Close();
            certificateStore.Dispose();

            if (cert == null)
            {
                _logger.Trace("------------ Error --------------   GetCertificate. Certificate not found");
            }

            return cert;
        }

        /// <summary>
        ///     Digital Wallet SSO request
        /// </summary>
        [HttpGet]
        [Route("v1/sso-request/{accountNumber}/{accountIdentifier}/{deviceIdentifier}")]
        public async Task<IHttpActionResult> GetDigitalWalletSsoRequestPayload(long accountNumber, string accountIdentifier, string deviceIdentifier)
        {
            if (!_settings.MobileConfiguration.DigitalWallet.Enabled)
            {
                return Ok("no access");
            }

            // endPointAddress for testing: = "https://cte-cardvalet-ws.fiservapps.com";
            var endPointAddress = _settings.MobileConfiguration.DigitalWallet.EndpointAddress;

            var userId = _settings.MobileConfiguration.DigitalWallet.userId;
            var decryptedPW = _cryptoProvider.DecryptString(_securityKey, _settings.MobileConfiguration.DigitalWallet.password, EncryptionType.Des);

            // refId example = "9999e999e99999e9e9eee99ee9e9999-9-9-0-9";
            var refId = accountIdentifier;

            // deviceId example = "yahd7864823bjn048pakln";
            var deviceId = deviceIdentifier;

            // testing: "99993576"   production: "99993575"
            var clientId = _settings.MobileConfiguration.DigitalWallet.clientId;

            // Log account number, account identifier and device identifier:
            _logger.Trace($"GetDigitalWalletSsoRequestPayload for {accountNumber}. AccountIdentifier: {accountIdentifier}. DeviceIdentifier: {deviceIdentifier}");

            var request = new DigitalWalletHeaderSignature
            {
                schemaVersion = _settings.MobileConfiguration.DigitalWallet.schemaVersion,
                clientId = clientId,
                system = _settings.MobileConfiguration.DigitalWallet.system.FirstOrDefault(),
                clientApplicationName = _settings.MobileConfiguration.DigitalWallet.clientApplicationName,
                clientVersion = _settings.MobileConfiguration.DigitalWallet.clientVersion,
                clientVendorName = _settings.MobileConfiguration.DigitalWallet.clientVendorName,
                clientAuditId = Guid.NewGuid().ToString().Replace("-", string.Empty).Substring(0, 12),
                subscriberRefID = refId,
                ssoDeviceId = deviceId
            };

            var resultString = await MakeSsoRequest(endPointAddress, userId, decryptedPW, request).ConfigureAwait(false);
            _logger.Trace($" ----- DigitalWalletController ------- resultString : {resultString}");

            if (resultString != null)
            {
                var result = JsonConvert.DeserializeObject<DigitalWalletSsoResponse>(resultString);

                var cardValetSsoResponse = new CardValetSsoResponse();

                if (result.CsStatus.StatusCode == "0")
                {
                    cardValetSsoResponse.SsoPayload = result.SsoPayload;
                    cardValetSsoResponse.AndroidStoreUrl = _settings.MobileConfiguration.DigitalWallet.AndroidStoreUrl;
                    cardValetSsoResponse.IosStoreUrl = _settings.MobileConfiguration.DigitalWallet.IosStoreUrl;
                    cardValetSsoResponse.UrlScheme = _settings.MobileConfiguration.DigitalWallet.UrlScheme;
                    cardValetSsoResponse.PackageName = _settings.MobileConfiguration.DigitalWallet.PackageName;
                }
                else
                {
                    cardValetSsoResponse.StatusDescription = result.CsStatus.StatusDesc;
                }

                return Ok(cardValetSsoResponse);
            }

            return BadRequest();
        }

#pragma warning disable CA1054 // Uri parameters should not be strings except in controller methods
#pragma warning disable CA1822 // Controller methods must not be static

        /// <summary>
        ///     Make Async Restful call:
        /// </summary>
        private async Task<string> MakeSsoRequest(string url, string userId, string password, DigitalWalletHeaderSignature data)
#pragma warning restore CA1054 // Uri parameters should not be strings
#pragma warning restore CA1822 // Controller methods must not be static
        {
            var certificatePW = _settings.MobileConfiguration.DigitalWallet.CertificatePassword;
            var clientCertificate = GetCertificate(_settings.MobileConfiguration.DigitalWallet.CertificateName);

            try
            {
                var client = new RestClient(url) { ClientCertificates = new X509CertificateCollection { clientCertificate } };
                var rqst = new RestRequest("rws/CardControlRWS_V0103/getSSOInfo", Method.POST);

                _logger.Trace($" ----- DigitalWalletController ------- DigitalWalletController -> MakeSsoRequest(). userId : {userId}. decrypted password: {password}");
                client.Authenticator = new HttpBasicAuthenticator(userId, password);
                client.Proxy = new WebProxy();
                rqst.AddHeader("Accept", "application/json");
                rqst.AddHeader("Content-Type", "application/json");

                rqst.AddJsonBody(data);

                var response = await client.ExecuteTaskAsync(rqst).ConfigureAwait(false);
                _logger.Trace($" ----- DigitalWalletController ------- DigitalWalletController -> MakeSsoRequest(). Response: {response.Content}.");

                return response.Content;

                // the response object should look like this:
                // {
                // schemaVersion = "2.0.0",
                // clientId = "999999",
                // system = "EPOC_CM",
                // clientApplicationName = "Connect Banking",
                // clientVersion = "1.0",
                // clientVendorName = "Connect FSS",
                // clientAuditId = "1234",
                // systemRecordIdentifier: null,
                // csStatus: {
                // statusCode = "0",
                // statusDesc = "SUCCESSFUL"
                // },
                // subscriberRefId = "9999e999e99999e9e9eee99ee9e9999-9-9-0-0",
                // ssoPayload = "lcG6qbAYu07WETKDENJDhlGi3dsVRbvxnBMXQVByhS 1A="
                // }
            }
            catch (Exception ex)
            {
                _logger.Error($" ----- DigitalWalletController ------- Error Calling DigitalWalletController -> MakeSsoRequest(). Error: {ex}.");
                return "{ \"csStatus\" : { \"statusCode\" : \"1\", \"statusDesc\" : \"failure\"} }";
            }
        }
    }
}