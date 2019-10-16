﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.Protocols;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SearchOption = System.DirectoryServices.Protocols.SearchOption;

namespace SharpHound3
{
    internal class DirectorySearch
    {
        private readonly string _domainController;
        private readonly string _domainName;
        private readonly Domain _domain;
        private Dictionary<string, string> _domainGuidMap;

        public DirectorySearch(string domainName = null, string domainController = null)
        {
            _domainName = domainName;
            _domain = GetDomain();
            _domainName = _domain.Name;
            _domainController = domainController;
            CreateSchemaMap();
        }

        internal IEnumerable<SearchResultEntry> QueryLdap(string ldapFilter, string[] props, SearchScope scope, string adsPath = null)
        {
            using (var connection = GetLdapConnection())
            {
                var searchRequest = CreateSearchRequest(ldapFilter, scope, props);
                var pageRequest = new PageResultRequestControl(500);
                searchRequest.Controls.Add(pageRequest);
                var securityDescriptorFlagControl = new SecurityDescriptorFlagControl
                {
                    SecurityMasks = SecurityMasks.Dacl | SecurityMasks.Owner
                };
                searchRequest.Controls.Add(securityDescriptorFlagControl);

                while (true)
                {
                    SearchResponse searchResponse;
                    try
                    {
                        searchResponse = (SearchResponse) connection.SendRequest(searchRequest);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("\nUnexpected exception occured:\n\t{0}: {1}",
                            e.GetType().Name, e.Message);
                        yield break;
                    }
                    
                    if (searchResponse.Controls.Length != 1 ||
                        !(searchResponse.Controls[0] is PageResultResponseControl))
                    {
                        Console.WriteLine("Server does not support paging");
                        yield break;
                    }

                    var pageResponse = (PageResultResponseControl) searchResponse.Controls[0];

                    foreach (SearchResultEntry entry in searchResponse.Entries)
                    {
                        yield return entry;
                    }

                    if (pageResponse.Cookie.Length == 0)
                        break;

                    pageRequest.Cookie = pageResponse.Cookie;
                }
            }
        }

        internal IEnumerable<string> RetrieveRangedAttribute(string distinguishedName, string attribute)
        {
            try
            {
                //Try ASQ first
                return RangeRetrievalAsq(distinguishedName, attribute);
            }
            catch
            {
                try
                {
                    return RangeRetrievalFallback(distinguishedName, attribute);
                }
                catch
                {
                    return null;
                }
            }
        }


        /// <summary>
        /// Attempt to retrieve an LDAP attribute from a DN using an Attribute Scoped Query
        /// </summary>
        /// <param name="distinguishedName"></param>
        /// <param name="attribute"></param>
        /// <returns></returns>
        private IEnumerable<string> RangeRetrievalAsq(string distinguishedName, string attribute)
        {
            using (var connection = GetLdapConnection())
            {
                var searchRequest = CreateSearchRequest("(&)", SearchScope.Base, null, distinguishedName);
                var asq = new AsqRequestControl(attribute);
                searchRequest.Controls.Add(asq);

                var searchResponse = (SearchResponse) connection.SendRequest(searchRequest);

                if (searchResponse.Controls.Length != 1 || !(searchResponse.Controls[0] is AsqResponseControl))
                {
                    //The domain controller doesn't support ASQ for some reason. So fall back to old fashioned LDAP retrieval.
                    throw new ControlNotSupportedException();
                }

                //var asqResponse = (AsqResponseControl) searchResponse.Controls[0];

                foreach (SearchResultEntry entry in searchResponse.Entries)
                {
                    yield return entry.DistinguishedName;
                }
            }
        }

        /// <summary>
        /// Attempt to retrieve a ranged LDAP attribute using old school ranged retrieval
        /// </summary>
        /// <param name="distinguishedName"></param>
        /// <param name="attribute"></param>
        /// <returns></returns>
        private IEnumerable<string> RangeRetrievalFallback(string distinguishedName, string attribute)
        {
            var index = 0;
            var step = 0;
            var baseString = $"{attribute};";
            var currentRange = $"{baseString};range={index}-*";

            using (var connection = GetLdapConnection())
            {
                var searchRequest = CreateSearchRequest($"{attribute}=*", SearchScope.Base, new string[] { currentRange },
                    distinguishedName);

                var searchDone = false;

                while (true)
                {
                    var response = (SearchResponse)connection.SendRequest(searchRequest);

                    if (response?.Entries.Count == 1)
                    {
                        var entry = response.Entries[0];

                        foreach (string attr in entry.Attributes.AttributeNames)
                        {
                            currentRange = attr;
                            searchDone = currentRange.IndexOf("*", 0, StringComparison.Ordinal) > 0;
                            step = entry.Attributes[currentRange].Count;
                        }

                        foreach (string member in entry.Attributes[currentRange].GetValues(typeof(string)))
                        {
                            yield return member;
                            index++;
                        }

                        if (searchDone)
                            yield break;

                        currentRange = $"{baseString};range={index}-{index + step}";

                        searchRequest.Attributes.Clear();
                        searchRequest.Attributes.Add(currentRange);
                    }
                    else
                        yield break;
                }
            }
        }

        internal bool GetNameFromGuid(string guid, out string name)
        {
            return _domainGuidMap.TryGetValue(guid, out name);
        }

        private Domain GetDomain()
        {
            try
            {
                if (_domainName == null)
                    return Domain.GetCurrentDomain();

                var context = new DirectoryContext(DirectoryContextType.Domain, _domainName);
                return Domain.GetDomain(context);
            }
            catch
            {
                return null;
            }
        }

        private DirectoryContext GetDomainContext()
        {
            return new DirectoryContext(DirectoryContextType.Domain, _domainName);
        }

        private LdapConnection GetLdapConnection()
        {
            var domainController = _domainController ?? _domainName;
            var identifier = new LdapDirectoryIdentifier(domainController, false, false);
            var connection = new LdapConnection(identifier);

            var ldapSessionOptions = connection.SessionOptions;
            ldapSessionOptions.Signing = true;
            ldapSessionOptions.Sealing = true;
            ldapSessionOptions.ProtocolVersion = 3;
            ldapSessionOptions.ReferralChasing = ReferralChasingOptions.None;
            
            connection.Timeout = new TimeSpan(0,5,0);
            return connection;
        }

        private SearchRequest CreateSearchRequest(string ldapFilter, SearchScope scope, string[] props, string adsPath=null)
        {
            var activeDirectorySearchPath = adsPath ?? $"DC={_domainName.Replace(".", ",DC=")}";
            var request = new SearchRequest(activeDirectorySearchPath, ldapFilter, scope, props);
            request.Controls.Add(new SearchOptionsControl(SearchOption.DomainScope));

            return request;
        }

        private void CreateSchemaMap()
        {
            var map = new Dictionary<string, string>();
            var path = _domain.Forest.Schema.Name;

            foreach (var result in QueryLdap("(schemaIDGUID=*)", new[] {"schemaidguid", "name"}, SearchScope.Subtree,
                path))
            {
                var name = result.GetProperty("name");
                var guid = new Guid(result.GetPropertyAsBytes("schemaidguid")).ToString();
                map.Add(guid, name);
            }

            _domainGuidMap = map;
        }
    }
}