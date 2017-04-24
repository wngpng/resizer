// Copyright (c) Imazen LLC.
// No part of this project, including this file, may be copied, modified,
// propagated, or distributed except as permitted in COPYRIGHT.txt.
// Licensed under the GNU Affero General Public License, Version 3.0.
// Commercial licenses available at http://imageresizing.net/
using ImageResizer.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Numerics;
using ImageResizer.Configuration.Issues;
using ImageResizer.Resizing;
using System.Drawing;
using ImageResizer.Plugins.Basic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;

namespace ImageResizer.Plugins.LicenseVerifier {

    internal class DomainNormalizer
    {
        // Cache of DNS hostnames to relevant licensed domains
        ConcurrentDictionary<string, string> normalized_domains = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> licensedDomains;
        Dictionary<string, string> mappings;
        internal Dictionary<string, string> DomainMappings { get { return mappings; } }
        public DomainNormalizer(Config c, IIssueReceiver sink, IEnumerable<string> licensedDomains)
        {
            this.licensedDomains = licensedDomains;
            mappings = GetDomainMappings(c, sink);
        }

        internal Dictionary<string, string> GetDomainMappings(Config c, IIssueReceiver sink) //c.configurationSectionIssue
        {
            Dictionary<string, string> mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var n = c.getNode("licenses");
            if (n == null) return mappings;
            foreach (var map in n.childrenByName("maphost"))
            {
                var from = map.Attrs["from"];
                var to = map.Attrs["to"];
                if (from == null || to == null)
                {
                    sink.AcceptIssue(new Issue("Both from= and to= attributes are required on maphost: " + map.ToString(), IssueSeverity.ConfigurationError));

                }
                else
                {
                    from = from.ToLowerInvariant();
                    if (from.Replace(".local", "").IndexOf('.') > -1)
                    {
                        sink.AcceptIssue(new Issue("You can only map non-public hostnames to arbitrary licenses. Skipping " + from, IssueSeverity.ConfigurationError));
                    }
                    else
                    {
                        mappings[from] = to;
                    }
                }
            }
            return mappings;
        }
        public string RemapDomain(string domain)
        {
            if (mappings.ContainsKey(domain))
            {
                return mappings[domain];
            }else
            {
                return domain;
            }
        }

        public string NormalizeDomain(string domain)
        {
            string normal;
            if (normalized_domains.TryGetValue(domain, out normal)) return normal;
 
            normal = domain.ToLowerInvariant();

            if (licensedDomains.Contains(domain))
            {
                return domain;
            }else { 
                //Try to find the first licensed that is a subset of the provided domain
                normal = licensedDomains.Where(d => normal.EndsWith(d, StringComparison.Ordinal)).FirstOrDefault(d => normal.EndsWith("." + d, StringComparison.Ordinal)) ?? normal;
            }
            normalized_domains[domain] = normal;
            return normal;
        }
        public static string NormalizeLicenseDomain(string domain)
        {
            var d = domain.ToLowerInvariant().TrimStart('.');
            if (d.StartsWith("www."))
            {
                d = d.Substring(4);
            }
            return d;
        }

    }

    internal class LicenseComputation : IDiagnosticsProvider
    {
        //Input
        Config c;
        IEnumerable<RSADecryptPublic> trustedKeys;
        IIssueReceiver sink;

        //Cached computations
        DomainNormalizer domains;
        /// <summary>
        /// List of lists. One of each child list should be present.
        /// </summary>
        IEnumerable<IEnumerable<string>> _installed_features;
        IList<ILicenseChain> chains;
        ILicenseManager mgr;
        IDictionary<string, ISet<string>> _licensedFeaturesByDomain;

        // TODO: distinguish betwen resolvable errors (perhaps missing license goes away?) and one-time 
        public LicenseComputation(Config c, IEnumerable<RSADecryptPublic> trustedKeys, IIssueReceiver sink, ILicenseManager mgr)
        {
            this.c = c;
            this.trustedKeys = trustedKeys;
            this.sink = sink;
            

            // What features are installed on this instance?
            _installed_features = c.Plugins.GetAll<ILicensedPlugin>().Select(p => p.LicenseFeatureCodes).ToList();
           
            // Empty() unless Config is reading shared licenses
            var shared = c.Plugins.LicenseScope.HasFlag(LicenseAccess.ProcessReadonly) ? mgr.GetSharedLicenses() : Enumerable.Empty<ILicenseChain>();

            // Create or fetch all relevant license chains; ignore the empty/invalid ones, they're logged to the manager instance
            chains = c.Plugins.GetAll<ILicenseProvider>().SelectMany(p => p.GetLicenses()).Select((str) => mgr.Add(str, c.Plugins.LicenseScope)).Where((x) => x != null).Concat(shared).ToList();
            this.mgr = mgr;

            var licensedDomains = chains.SelectMany((x) => x.Licenses().Select((b) => b.GetParsed().Get("Domain"))).Where((s) => !string.IsNullOrWhiteSpace(s)).Select((s) => DomainNormalizer.NormalizeLicenseDomain(s)).ToList();
            this.domains = new DomainNormalizer(c, sink, licensedDomains);

            _licensedFeaturesByDomain = new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (ILicenseBlob blob in chains.Select((x) => SelectLicense(x, sink)).Where((x) => x != null))
            {
                var dict = _licensedFeaturesByDomain;
                var details = blob.GetParsed();
                var domain = string.IsNullOrEmpty(details.Get("Domain")) ? "*" : DomainNormalizer.NormalizeLicenseDomain(details.Get("Domain"));
                var features = details.Get("Features")?.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                // Merge with existing features associated with domain
                if (features != null)
                {
                    dict[domain] = new HashSet<string>(Enumerable.Concat(features, dict.ContainsKey(domain) ? dict[domain] : Enumerable.Empty<string>()));
                }
            }
        }

        private bool IsValid(ILicenseBlob blob, IIssueReceiver sink)
        {

            //kind: id (expiry based on 
            
            StringBuilder log = sink != null ? new StringBuilder() : null;
            bool valid_signature = new LicenseValidator().Validate(blob, trustedKeys, log);
            var details = blob.GetParsed(); ///TODO: reparse if 

            bool expired = details.Expires < DateTime.UtcNow;
            bool invalid_time = details.Issued > DateTime.UtcNow;

            if (expired || invalid_time)
            {
                sink?.AcceptIssue(new Issue("License key " + (expired ? "has expired: " : "was issued in the future; check system clock: ") + UTF8Encoding.UTF8.GetString(blob.GetDataUTF8()), IssueSeverity.Warning));
            }
            if (!valid_signature)
            {
                sink?.AcceptIssue(new Issue("Invalid license key: failed to validate signature.", log.ToString(), IssueSeverity.Error));
            }
            
            return (valid_signature && !invalid_time && !expired);
        }

        private ILicenseBlob SelectLicense(ILicenseChain c, IIssueReceiver sink)
        {
            // If there is a fresh license, then *only* apply that license; OTHERWISE apply offline logic

            var remote = c.GetFreshRemoteLicense();
            if (remote != null) return remote;

            //TODO; review all
            // Filter by validity
            // Filter by expiry date

            // Replace 'secret' key with fetched key, unless fetched key is expired? 

            //TODO: Validate build date
            //TODO: Validate grace period against first heartbeat

            var first = c.Licenses().Where((b) => IsValid(b, sink)).FirstOrDefault();
            return first;
            // Otherwise, 


          }

   
        public bool LicensedForHost(string domain)
        {

            var possiblyLicensed = domains.NormalizeDomain(domains.RemapDomain(domain));

            return FeaturesLicensedForDomain(possiblyLicensed, this._installed_features);
        }

        internal bool FeaturesLicensedForDomain(string normalizedDomain, IEnumerable<IEnumerable<string>> require_one_from_each_collection)
        {
            ISet<string> domain_features;
            bool found = false;
            if (this._licensedFeaturesByDomain.TryGetValue(normalizedDomain, out domain_features))
            {
                found = require_one_from_each_collection.All(coll => domain_features.Intersect(coll).Count() > 0);
            }
            if (!found && this._licensedFeaturesByDomain.TryGetValue("*", out domain_features))
            {
                found = require_one_from_each_collection.All(coll => domain_features.Intersect(coll).Count() > 0);
            }
            if (!found)
            {
                sink.AcceptIssue(new Issue(string.Format("No license found for domain '{0}' - features installed: {1}", normalizedDomain, String.Join(" AND ", require_one_from_each_collection.Select(coll => String.Join(" or ", coll)))), IssueSeverity.Error));
            }
            return found;
        }

   

      

        public string GetLicensedFeaturesDescription()
        {
            return String.Join("\n",_licensedFeaturesByDomain.Select(pair => String.Format("{0} => {1}", pair.Key, String.Join(" ", pair.Value))));
        }

        public string ProvideDiagnostics()
        {
            StringBuilder sb = new StringBuilder();



            var mappings = domains.DomainMappings;
            
            sb.AppendLine("\n----------------\n");
            sb.AppendLine("Licenses");
            if (mappings.Count > 0)
            {
                sb.AppendLine("For domain licensing, you have mapped the following local (non-public) domains or addresses as follows:\n" +
                    String.Join(", ", mappings.Select(pair => string.Format("{0} => {1}", pair.Key, pair.Value))));
            }

            var licenses = GetLicensedFeaturesDescription();
            sb.AppendLine();
            if (licenses.Length > 0)
            {
                sb.AppendLine("List of licensed features by domain:\n" + licenses);
            }
            else
            {
                sb.AppendLine("You do not have any active license keys installed.");

            }

            if (chains.Count > 0)
            {
                sb.AppendLine("List of licenses for this Config instance:\n" + licenses);
                sb.AppendLine(string.Join("\n", chains.Select(c => c.ToString())));
            }
            var others = mgr.GetAllLicenses().Except(chains);
            if (others.Count() > 0)
            {
                sb.AppendLine("Licenses only used by other Config instances in this procces:\n" + licenses);
                sb.AppendLine(string.Join("\n", chains.Select(c => c.ToString())));
            }

            var mgrs = mgr as LicenseManagerSingleton;
            if (mgrs != null)
            {
                sb.AppendFormat("{0} heartbeat events. First {1} ago.\n", mgrs.HeartbeatCount, mgrs.FirstHeartbeat == null ? "(never)" : DateTime.UtcNow.Subtract(mgrs.FirstHeartbeat.Value).ToString());
            }
            sb.AppendLine("\n----------------\n");
            return sb.ToString();
        }
    }


    internal class LicenseEnforcer<T> : BuilderExtension, IPlugin, IDiagnosticsProvider, IIssueProvider
    {

        private ILicenseManager mgr;
        public LicenseEnforcer() { mgr = LicenseManagerSingleton.Singleton; }
        public LicenseEnforcer(ILicenseManager mgr){ this.mgr = mgr; }



        private Config c;
        private IIssueReceiver Sink { get { return c.configurationSectionIssues;  } }
        private LicenseComputation cache;

       
        DateTime first_request = DateTime.MinValue;
        int invalidated_count = 0;
        private bool ShouldDisplayDot(Config c, ImageState s)
        {
            if (c == null || c.configurationSectionIssues == null || System.Web.HttpContext.Current == null) return false;

            //We want to invalidate the caches after 5 and 30 seconds.
            if (first_request == DateTime.MinValue) first_request = DateTime.UtcNow;
            bool invalidate = invalidated_count == 0 && DateTime.UtcNow - first_request > TimeSpan.FromSeconds(5) ||
                invalidated_count == 1 && DateTime.UtcNow - first_request > TimeSpan.FromSeconds(30);


            //Cache a LicenseService and nested enumeration of installed feature codes
            if (invalidate)
            {
                invalidated_count++;
                Invalidate();
            }

            var domain = System.Web.HttpContext.Current.Request.Url.DnsSafeHost;

            return cache.LicensedForHost(domain);

        }

        const string settings_key = "red_dot";
        const string settings_value = "1";
        protected override RequestedAction PreFlushChanges(ImageState s)
        {
            
            if (s.destBitmap != null && s.settings[settings_key] == settings_value)
            {
                int w = s.destBitmap.Width;
                int h = s.destBitmap.Height;
                int dot_w = 3;
                int dot_h = 3;
                if (w > dot_w && h > dot_h)
                {
                    for (int y = 0; y < dot_h; y++)
                        for (int x = 0; x < dot_w; x++ )
                            s.destBitmap.SetPixel(w - 1 - x, h - 1 - y, Color.Red);
                }
                s.settings[settings_key] = "done";
            }
            return RequestedAction.None;
        }

        private void Invalidate()
        {
            //TODO: !! change to production
            cache = new LicenseComputation(this.c, ImazenPublicKeys.Test, this.Sink, this.mgr);
        }

        public IPlugin Install(Config c)
        {
            this.c = c;
            Invalidate();
            c.Plugins.add_plugin(this);
            c.Pipeline.PostRewrite += Pipeline_PostRewrite;
            c.Pipeline.Heartbeat += Pipeline_Heartbeat;
            c.Plugins.LicensingChange += Plugins_LicensingChange;
            return this;
        }

        private void Pipeline_Heartbeat(IPipelineConfig sender, Config c)
        {
            mgr.Heartbeat();
        }

        private void Plugins_LicensingChange(object sender, Config forConfig)
        {
            Invalidate();
        }

        private void Pipeline_PostRewrite(System.Web.IHttpModule sender, System.Web.HttpContext context, IUrlEventArgs e)
        {
            if (ShouldDisplayDot(this.c, null))
            {
                e.QueryString[settings_key] = settings_value;
            }
        }
        public bool Uninstall(Configuration.Config c)
        {
            c.Plugins.remove_plugin(this);
            return true;
        }

        public string ProvideDiagnostics()
        {
            return cache.ProvideDiagnostics();
        }

        public IEnumerable<IIssue> GetIssues()
        {
            return mgr.GetIssues();
        }
    }
}
