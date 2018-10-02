﻿using ARMExplorer.Controllers;
using ARMExplorer.Modules;
using LetsEncrypt.Azure.Core;
using LetsEncrypt.Azure.Core.Models;
using LetsEncrypt.SiteExtension.Models;
using Microsoft.Azure.Graph.RBAC;
using Microsoft.Azure.Graph.RBAC.Models;
using Microsoft.Azure.Management.Resources;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using Microsoft.Rest.Azure;
using Microsoft.Rest.Azure.Authentication;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace LetsEncrypt.SiteExtension.Controllers
{
    public class HomeController : Controller
    {
        // GET: Authentication
        public ActionResult Index()
        {
            var model = (AuthenticationModel)new AppSettingsAuthConfig();
            return View(model);
        }

        [HttpPost]
        public ActionResult Index(AuthenticationModel model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    using (var client = ArmHelper.GetWebSiteManagementClient(model))
                    {
                        //Update web config.
                        var site = client.WebApps.GetSiteOrSlot(model.ResourceGroupName, model.WebAppName, model.SiteSlotName);
                        //Validate that the service plan resource group name is correct, to avoid more issues on this specific problem.
                        var azureServerFarmResourceGroup = site.ServerFarmResourceGroup();
                        if (!string.Equals(azureServerFarmResourceGroup, model.ServicePlanResourceGroupName, StringComparison.InvariantCultureIgnoreCase))
                        {
                            ModelState.AddModelError("ServicePlanResourceGroupName", string.Format("The Service Plan Resource Group registered on the Web App in Azure in the ServerFarmId property '{0}' does not match the value you entered here {1}", azureServerFarmResourceGroup, model.ServicePlanResourceGroupName));
                            return View(model);
                        }
                        var webappsettings = client.WebApps.ListSiteOrSlotAppSettings(model.ResourceGroupName, model.WebAppName, model.SiteSlotName);
                        if (model.UpdateAppSettings)
                        {
                            var newAppSettingsValues = new Dictionary<string, string>{
                                { AppSettingsAuthConfig.clientIdKey, model.ClientId.ToString() },
                                { AppSettingsAuthConfig.clientSecretKey, model.ClientSecret.ToString()},
                                { AppSettingsAuthConfig.subscriptionIdKey, model.SubscriptionId.ToString() },
                                { AppSettingsAuthConfig.tenantKey, model.Tenant },
                                { AppSettingsAuthConfig.resourceGroupNameKey, model.ResourceGroupName },
                                { AppSettingsAuthConfig.siteSlotNameKey, model.SiteSlotName},
                                { AppSettingsAuthConfig.servicePlanResourceGroupNameKey, model.ServicePlanResourceGroupName },
                                { AppSettingsAuthConfig.useIPBasedSSL, model.UseIPBasedSSL.ToString().ToLowerInvariant() }
                            };

                            // if the user changed the default domain then we create the app settings for it.
                            if (model.AzureWebSitesDefaultDomainName != "azurewebsites.net" && !string.IsNullOrWhiteSpace(model.AzureWebSitesDefaultDomainName))
                            {
                                newAppSettingsValues.Add(AppSettingsAuthConfig.azureDefaultWebSiteDomainName, model.AzureWebSitesDefaultDomainName);
                            }

                            foreach (var appsetting in newAppSettingsValues)
                            {
                                if (!webappsettings.Properties.ContainsKey(appsetting.Key))
                                {
                                    webappsettings.Properties.Add(appsetting.Key, appsetting.Value);
                                }
                                else
                                {
                                    webappsettings.Properties[appsetting.Key] = appsetting.Value;
                                }
                            }

                            client.WebApps.UpdateSiteOrSlotAppSettings(model.ResourceGroupName, model.WebAppName, model.SiteSlotName, webappsettings);
                            ConfigurationManager.RefreshSection("appSettings");                            
                        }
                        else
                        {
                            var appSetting = new AppSettingsAuthConfig();
                            if (!ValidateModelVsAppSettings("ClientId", appSetting.ClientId.ToString(), model.ClientId.ToString()) ||
                            !ValidateModelVsAppSettings("ClientSecret", appSetting.ClientSecret, model.ClientSecret) ||
                            !ValidateModelVsAppSettings("ResourceGroupName", appSetting.ResourceGroupName, model.ResourceGroupName) ||
                            !ValidateModelVsAppSettings("SubScriptionId", appSetting.SubscriptionId.ToString(), model.SubscriptionId.ToString()) ||
                            !ValidateModelVsAppSettings("Tenant", appSetting.Tenant, model.Tenant) ||
                            !ValidateModelVsAppSettings("SiteSlotName", appSetting.SiteSlotName, model.SiteSlotName) ||
                            !ValidateModelVsAppSettings("ServicePlanResourceGroupName", appSetting.ServicePlanResourceGroupName, model.ServicePlanResourceGroupName) ||
                            !ValidateModelVsAppSettings("UseIPBasedSSL", appSetting.UseIPBasedSSL.ToString().ToLowerInvariant(), model.UseIPBasedSSL.ToString().ToLowerInvariant()))
                            {
                                model.ErrorMessage = "One or more app settings are different from the values entered, do you want to update the app settings?";
                                return View(model);
                            }
                        }


                    }
                    return RedirectToAction("PleaseWait");
                }
                catch (Exception ex)
                {
                    Trace.TraceError(ex.ToString());
                    model.ErrorMessage = ex.ToString();
                }

            }

            return View(model);
        }

        private bool ValidateModelVsAppSettings(string name, string appSettingValue, string modelValue)
        {
            if (string.IsNullOrEmpty(appSettingValue) && string.IsNullOrEmpty(modelValue))
            {
                return true;
            }
            if (appSettingValue != modelValue)
            {
                ModelState.AddModelError(name, string.Format("The {0} registered under application settings {1} does not match the {0} you entered here {2}", name, appSettingValue, modelValue));
                return false;
            }
            return true;
        }

        public ActionResult PleaseWait()
        {
            var settings = new AppSettingsAuthConfig();
            List<ValidationResult> validationResult = null;
            if (settings.IsValid(out validationResult))
            {
                return RedirectToAction("Hostname");
            }

            return View();
        }

        public ActionResult Hostname(string id)
        {
            var settings = new AppSettingsAuthConfig();
            var model = new HostnameModel();
            List<ValidationResult> validationResult = null;
            if (settings.IsValid(out validationResult))
            {
                var client = ArmHelper.GetWebSiteManagementClient(settings);

                var site = client.WebApps.GetSiteOrSlot(settings.ResourceGroupName, settings.WebAppName, settings.SiteSlotName);               
                model.HostNames = site.HostNames;
                model.HostNameSslStates = site.HostNameSslStates;
                model.Certificates = client.Certificates.ListByResourceGroup(settings.ServicePlanResourceGroupName).ToList();
                model.InstalledCertificateThumbprint = id;
                if (model.HostNames.Count == 1)
                {
                    model.ErrorMessage = "No custom host names registered. At least one custom domain name must be registed for the web site to request a letsencrypt certificate.";
                }

            }
            else
            {
                var errorMessage = string.Join(" ,", validationResult.Select(s => s.ErrorMessage));
                model.ErrorMessage = $"Application settings was invalid, please wait a little and try to reload page if you just updated appsettings. Validation errors: {errorMessage}";
            }

            return View(model);
        }

        public ActionResult Install()
        {
            SetViewBagHostnames();
            var emailSettings = SettingsStore.Instance.Load().FirstOrDefault(s => s.Name == "email");
            string email = string.Empty;
            if (emailSettings != null)
            {
                email = emailSettings.Value;
            }
            return View(new RequestAndInstallModel()
            {
                Email = email
            }
            );
        }

        private void SetViewBagHostnames()
        {
            var settings = new AppSettingsAuthConfig();
            var client = ArmHelper.GetWebSiteManagementClient(settings);

            var site = client.WebApps.GetSiteOrSlot(settings.ResourceGroupName, settings.WebAppName, settings.SiteSlotName);
            var model = new HostnameModel();
            ViewBag.HostNames = site.HostNames.Where(s => !s.EndsWith(settings.AzureWebSitesDefaultDomainName)).Select(s => new SelectListItem()
            {
                Text = s,
                Value = s
            });
        }

        [HttpPost]
        public async Task<ActionResult> Install(RequestAndInstallModel model)
        {
            if (ModelState.IsValid)
            {
                var s = SettingsStore.Instance.Load();
                s.Clear();
                s.Add(new SettingEntry()
                {
                    Name = "email",
                    Value = model.Email
                });
                var baseUri = model.UseStaging == false ? "https://acme-v01.api.letsencrypt.org/" : "https://acme-staging.api.letsencrypt.org/";
                s.Add(new SettingEntry()
                {
                    Name = "baseUri",
                    Value = baseUri
                });
                SettingsStore.Instance.Save(s);
                var settings = new AppSettingsAuthConfig();
                var target = new AcmeConfig()
                {                    
                    RegistrationEmail = model.Email,
                    Host = model.Hostnames.First(),                    
                    BaseUri = baseUri,                    
                    AlternateNames = model.Hostnames.Skip(1).ToList(),
                    PFXPassword = settings.PFXPassword,
                    RSAKeyLength = settings.RSAKeyLength,    
                };
                var thumbprint = await new CertificateManager(settings).RequestAndInstallInternalAsync(target);
                if (thumbprint != null)
                    return RedirectToAction("Hostname", new { id = thumbprint });
            }
            SetViewBagHostnames();
            return View(model);
        }

        public ActionResult AddHostname()
        {
            var settings = new AppSettingsAuthConfig();
            using (var client = ArmHelper.GetWebSiteManagementClient(settings))
            {
                var s = client.WebApps.GetSiteOrSlot(settings.ResourceGroupName, settings.WebAppName, settings.SiteSlotName);
                foreach (var hostname in settings.Hostnames)
                {
                    client.WebApps.CreateOrUpdateSiteOrSlotHostNameBinding(settings.ResourceGroupName, settings.WebAppName, settings.SiteSlotName, hostname, new HostNameBinding
                    {
                        CustomHostNameDnsRecordType = CustomHostNameDnsRecordType.CName,
                        HostNameType = HostNameType.Verified,
                        SiteName = settings.WebAppName,
                        Location = s.Location
                    });
                }
            }
            return View();
        }

        public ActionResult CreateServicePrincipal()
        {
            var head = Request.Headers.GetValues(Utils.X_MS_OAUTH_TOKEN).FirstOrDefault();

            var client = new SubscriptionClient(new TokenCredentials(head));
            client.SubscriptionId = Guid.NewGuid().ToString();
            var tenants = client.Tenants.List();


            var subs = client.Subscriptions.List();
            var cookie = ARMOAuthModule.ReadOAuthTokenCookie(HttpContext.ApplicationInstance);

            //var graphToken = AADOAuth2AccessToken.GetAccessTokenByRefreshToken(cookie.TenantId, cookie.refresh_token, "https://graph.windows.net/");

            var settings = ActiveDirectoryServiceSettings.Azure;
            var authContext = new AuthenticationContext(settings.AuthenticationEndpoint + "common");
            var graphToken = authContext.AcquireToken("https://management.core.windows.net/", new ClientCredential("d1b853e2-6e8c-4e9e-869d-60ce913a280c", "hVAAmWMFjX0Z0T4F9JPlslfg8roQNRHgIMYIXAIAm8s="));


            var graphClient = new GraphRbacManagementClient(new TokenCredentials(graphToken.AccessToken));

            graphClient.SubscriptionId = subs.FirstOrDefault().SubscriptionId;
            graphClient.TenantID = tenants.FirstOrDefault().TenantId;
            //var servicePrincipals = graphClient.ServicePrincipal.List();
            try
            {
                var res = graphClient.Application.Create(new Microsoft.Azure.Graph.RBAC.Models.ApplicationCreateParameters()
                {
                    DisplayName = "Test Application created by ARM",
                    Homepage = "https://test.sjkp.dk",
                    AvailableToOtherTenants = false,
                    IdentifierUris = new string[] { "https://absaad12312.sjkp.dk" },
                    ReplyUrls = new string[] { "https://test.sjkp.dk" },
                    PasswordCredentials = new PasswordCredential[] { new PasswordCredential() {
                    EndDate = DateTime.UtcNow.AddYears(1),
                    KeyId = Guid.NewGuid().ToString(),
                    Value = "s3nheiser",
                    StartDate = DateTime.UtcNow
                } },
                });

            }
            catch (CloudException ex)
            {
                var s = ex.Body.Message;
                var s2 = ex.Response.Content;

            }

            return View();
        }
    }
}