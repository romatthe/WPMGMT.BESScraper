﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Xml.Linq;
using Dapper;
using NLog;
using RestSharp;
using WPMGMT.BESScraper.Model;

namespace WPMGMT.BESScraper.API
{
    class BesApi
    {
        // Fields
        private Uri baseURL;
        private HttpBasicAuthenticator authenticator;
        private Logger logger = LogManager.GetCurrentClassLogger();

        // Properties
        public Uri BaseURL
        {
            get { return this.baseURL; }
            private set { this.baseURL = value; }
        }

        public HttpBasicAuthenticator Authenticator
        {
            get { return this.authenticator; }
            private set { this.authenticator = value; }
        }

        // Constructors
        public BesApi(string aBaseURL, string aUsername, string aPassword)
        {
            // Use to ignore SSL errors if specified in App.config
            if (AppSettings.Get<bool>("IgnoreSSL"))
            {
                ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => true;
            }

            this.BaseURL = new Uri(aBaseURL);
            this.authenticator = new HttpBasicAuthenticator(aUsername, aPassword);
        }


        // Methods
        public WPMGMT.BESScraper.Model.Action GetAction(int id)
        {
            return GetActions().SingleOrDefault(x => x.ID == id);
        }

        //public List<WPMGMT.BESScraper.Model.Action> GetActions()
        //{
        //    RestClient client = new RestClient(this.BaseURL);
        //    client.Authenticator = this.Authenticator;

        //    RestRequest request = new RestRequest("actions", Method.GET);

        //    return Execute<List<WPMGMT.BESScraper.Model.Action>>(request);
        //}

        public List<WPMGMT.BESScraper.Model.Action> GetActions()
        {
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            List<WPMGMT.BESScraper.Model.Action> actions = new List<WPMGMT.BESScraper.Model.Action>();

            // We need to use Session Relevance to acquire the list of Sites, REST API sucks
            // We'll use the following Relevance query, no parameters are required:
            string relevance = "(((name of it) of site of it) of source fixlets of it, id of it, name of it) of BES Actions";

            // Let's compose the request string
            RestRequest request = new RestRequest("query", Method.GET);
            request.AddQueryParameter("relevance", relevance);

            XDocument response = Execute(request);

            // Let's check if the Result element is empty
            if (response.Element("BESAPI").Element("Query").Element("Result").Elements().Count() > 0)
            {
                // We'll need to fetch the list of Sites from the DB in order to retrieve the SiteID
                BesDb DB = new BesDb(ConfigurationManager.ConnectionStrings["DB"].ToString());

                // All answers are wrapped inside a "Tuple" element
                foreach (XElement tupleElement in response.Element("BESAPI").Element("Query").Element("Result").Elements("Tuple"))
                {
                    // The Result consists of three parts:
                    //  1) The Site Name
                    //  2) The ActionID
                    //  3) The Action Name
                    XElement siteElement = tupleElement.Elements("Answer").First();
                    XElement actionIDElement = tupleElement.Elements("Answer").ElementAt(1);
                    XElement valueElement = tupleElement.Elements("Answer").Last();

                    // Resolve Site Name to Site ID
                    Site dbSite = DB.Connection.Query<Site>("SELECT * FROM BESEXT.SITE WHERE @Name = Name", new { Name = siteElement.Value }).Single();
         
                    // Add the new action
                    actions.Add(new WPMGMT.BESScraper.Model.Action(Convert.ToInt32(actionIDElement.Value), dbSite.ID, valueElement.Value));
                }
            }

            return actions;
        }

        public ActionDetail GetActionDetail(WPMGMT.BESScraper.Model.Action action)
        {
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            RestRequest request = new RestRequest("action/{id}/status", Method.GET);
            request.AddUrlSegment("id", action.ActionID.ToString());

            // Execute the request
            XDocument response = Execute(request);

            ActionDetail result = new ActionDetail(
                            Int32.Parse(response.Element("BESAPI").Element("ActionResults").Element("ActionID").Value.ToString()),
                            response.Element("BESAPI").Element("ActionResults").Element("Status").Value.ToString(),
                            response.Element("BESAPI").Element("ActionResults").Element("DateIssued").Value.ToString());

            return result;
        }

        public List<ActionDetail> GetActionDetails(List<WPMGMT.BESScraper.Model.Action> actions)
        {
            List<ActionDetail> details = new List<ActionDetail>();

            foreach (WPMGMT.BESScraper.Model.Action action in actions)
            {
                details.Add(GetActionDetail(action));
            }

            return details;
        }

        public List<ActionResult> GetActionResults(WPMGMT.BESScraper.Model.Action action)
        {
            List<ActionResult> results = new List<ActionResult>();
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            RestRequest request = new RestRequest("action/{id}/status", Method.GET);
            request.AddUrlSegment("id", action.ActionID.ToString());

            // Execute the request
            XDocument response = Execute(request);

            foreach (XElement computerElement in response.Element("BESAPI").Element("ActionResults").Elements("Computer"))
            {
                DateTime startTime = new DateTime();
                DateTime endTime = new DateTime();

                if (computerElement.Element("StartTime") != null)
                {
                    startTime = Convert.ToDateTime(computerElement.Element("StartTime").Value.ToString());
                }
                if (computerElement.Element("EndTime") != null)
                {
                    endTime = Convert.ToDateTime(computerElement.Element("EndTime").Value.ToString());
                }

                results.Add(new ActionResult(
                                    action.ActionID,                                                            // Action ID
                                    Int32.Parse(computerElement.Attribute("ID").Value.ToString()),              // Computer ID
                                    computerElement.Element("Status").Value.ToString(),                         // Status
                                    Int32.Parse(computerElement.Element("ApplyCount").Value.ToString()),        // Times applied
                                    Int32.Parse(computerElement.Element("RetryCount").Value.ToString()),        // Times retried
                                    Int32.Parse(computerElement.Element("LineNumber").Value.ToString()),        // Which script line is being executed
                                    // Time execution started
                                    (computerElement.Element("StartTime") != null) ? Convert.ToDateTime(computerElement.Element("StartTime").Value.ToString()) : (DateTime?)null,
                                    // Time execution started
                                    (computerElement.Element("EndTime") != null) ? Convert.ToDateTime(computerElement.Element("EndTime").Value.ToString()) : (DateTime?)null
                    ));
            }

            return results;
        }

        public List<ActionResult> GetActionResults(List<WPMGMT.BESScraper.Model.Action> actions)
        {
            List<ActionResult> results = new List<ActionResult>();

            foreach (WPMGMT.BESScraper.Model.Action action in actions)
            {
                results.AddRange(GetActionResults(action));
            }

            return results;
        }

        public List<Analysis> GetAnalyses(List<Site> sites)
        {
            List<Analysis> analyses = new List<Analysis>();

            foreach (Site site in sites)
            {
                analyses.AddRange(GetAnalyses(site));
            }

            return analyses;
        }

        public List<Analysis> GetAnalyses(Site site)
        {
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            RestRequest request = new RestRequest("analyses/{sitetype}/{site}", Method.GET);
            request.AddUrlSegment("sitetype", site.Type);
            request.AddUrlSegment("site", site.Name);

            // TODO: Handle master action site properly
            if (site.Type == "master")
            {
                request = new RestRequest("analyses/{sitetype}", Method.GET);
                request.AddUrlSegment("sitetype", site.Type);
            }

            List<Analysis> analyses = Execute<List<Analysis>>(request);

            foreach (Analysis analysis in analyses)
            {
                analysis.SiteID = site.ID;
            }

            return analyses;
        }

        public List<AnalysisProperty> GetAnalysisProperties(List<Analysis> analyses)
        {
            List<AnalysisProperty> properties = new List<AnalysisProperty>();

            foreach (Analysis analysis in analyses)
            {
                properties.AddRange(GetAnalysisProperties(analysis));
            }

            return properties;
        }

        public List<AnalysisProperty> GetAnalysisProperties(Analysis analysis)
        {
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            // The API does not assign an ID to the Site. Therefore, we use the ID assigned by the DB.
            // For this reason we're fetching the list of sites from the DB again, so we can resolve ID->Name
            BesDb besDb = new BesDb(ConfigurationManager.ConnectionStrings["DB"].ToString());
            Site site = besDb.SelectSite(analysis.SiteID);

            // Likewise, we need the DB ID of the Analysis, because we API IDs are NOT unique
            analysis = besDb.SelectAnalysis(analysis.SiteID, analysis.AnalysisID);

            RestRequest request = new RestRequest("analysis/{sitetype}/{site}/{analysisid}", Method.GET);
            request.AddUrlSegment("sitetype", site.Type);
            request.AddUrlSegment("site", site.Name);
            request.AddUrlSegment("analysisid", analysis.AnalysisID.ToString());

            XDocument response = Execute(request);

            List<AnalysisProperty> properties = new List<AnalysisProperty>();

            foreach (XElement propertyElement in response.Element("BES").Element("Analysis").Elements("Property"))
            {
                properties.Add(new AnalysisProperty(
                                            analysis.ID,
                                            Convert.ToInt32(propertyElement.Attribute("ID").Value),
                                            propertyElement.Attribute("Name").Value)
                                        );
            }

            return properties;
        }

        public List<AnalysisPropertyResult> GetAnalysisPropertyResults(List<AnalysisProperty> properties)
        {
            List<AnalysisPropertyResult> results = new List<AnalysisPropertyResult>();

            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            foreach (AnalysisProperty property in properties)
            {
                results.AddRange(GetAnalysisPropertyResult(property));
            }

            return results;
        }

        // Returns results for all computers for said property
        public List<AnalysisPropertyResult> GetAnalysisPropertyResult(AnalysisProperty property)
        {
            logger.Debug("Collecting Property - {0}: Property: {1}", property.ID, property.Name);

            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            List<AnalysisPropertyResult> results = new List<AnalysisPropertyResult>();

            // We need to use Session Relevance to acquire property results
            // We'll use the following Relevance query:
            // {0}: The SequenceNo/Source ID of the Analysis property
            // {1}: The Name of the Analysis
            string relevance = "((id of it) of computer of it, values of it) of results from (BES Computers) of BES Properties whose ((source id of it = {0}) and (name of source analysis of it = \"{1}\"))";

            // Unfortunately, we'll also need the name of the Parent Analysis. For that, we'll need to query the DB
            BesDb besDb = new BesDb(ConfigurationManager.ConnectionStrings["DB"].ToString());
            Analysis analysis = besDb.SelectAnalysis(property.AnalysisID);

            // Let's compose the request string
            RestRequest request = new RestRequest("query", Method.GET);
            //request.AddQueryParameter("relevance", String.Format(relevance, computer.ComputerID.ToString(), property.SequenceNo.ToString(), analysis.Name));
            request.AddQueryParameter("relevance", String.Format(relevance, property.SequenceNo.ToString(), analysis.Name));

            XDocument response = Execute(request);

            // Let's check if the Result element is empty
            if (response.Element("BESAPI").Element("Query").Element("Result").Elements().Count() > 0)
            {
                // All answers are wrapped inside a "Tuple" element
                foreach (XElement tupleElement in response.Element("BESAPI").Element("Query").Element("Result").Elements("Tuple"))
                {
                    // The Result consists of two parts:
                    //  1) The ComputerID
                    //  2) The value of the retrieved property sequence for said ComputerID
                    XElement computerElement = tupleElement.Elements("Answer").First();
                    XElement valueElement = tupleElement.Elements("Answer").Last();
                    results.Add(new AnalysisPropertyResult(property.ID, Convert.ToInt32(computerElement.Value.ToString()), valueElement.Value.ToString()));
                }
            }

            return results;
        }

        public List<Baseline> GetBaselines(List<Site> sites)
        {
            List<Baseline> baselines = new List<Baseline>();

            // Loop through the complete list of sites provided
            foreach (Site site in sites)
            {
                baselines.AddRange(GetBaselines(site));
            }

            return baselines;
        }

        public List<Baseline> GetBaselines(Site site)
        {
            List<Baseline> baselines = new List<Baseline>();

            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;
            
            // The list of baselines is contained within the site content
            RestRequest request = new RestRequest("site/{sitetype}/{site}/content", Method.GET);
            request.AddUrlSegment("sitetype", site.Type);
            request.AddUrlSegment("site", site.Name);

            // TODO: Handle master action site properly
            if (site.Type == "master")
            {
                request = new RestRequest("site/{sitetype}/content", Method.GET);
                request.AddUrlSegment("sitetype", site.Type);
            }

            XDocument response = Execute(request);

            if (response.Element("BESAPI").Elements("Baseline") != null)
            {
                foreach (XElement baselineElement in response.Element("BESAPI").Elements("Baseline"))
                {
                    baselines.Add(new Baseline(
                                    Convert.ToInt32(baselineElement.Element("ID").Value),
                                    site.ID,
                                    baselineElement.Element("Name").Value
                                ));
                }
            }

            return baselines;
        }

        public List<BaselineResult> GetBaselineResults(List<Baseline> baselines)
        {
            List<BaselineResult> results = new List<BaselineResult>();

            foreach (Baseline baseline in baselines)
            {
                results.AddRange(GetBaselineResults(baseline));
            }

            return results;
        }

        public List<BaselineResult> GetBaselineResults(Baseline baseline)
        {
            List<BaselineResult> results = new List<BaselineResult>();
            
            // We need to acquire some info concerning the site
            // Let's fetch the site object now
            BesDb besDb = new BesDb(ConfigurationManager.ConnectionStrings["DB"].ToString());
            Site dbSite = besDb.SelectSite(baseline.SiteID);

            // The list of baselines is contained within the site content
            RestRequest request = new RestRequest("fixlet/{sitetype}/{site}/{baselineid}/computers", Method.GET);
            request.AddUrlSegment("sitetype", dbSite.Type);
            request.AddUrlSegment("site", dbSite.Name);
            request.AddUrlSegment("baselineid", baseline.BaselineID.ToString());

            // TODO: Handle master action site properly
            if (dbSite.Type == "master")
            {
                request = new RestRequest("fixlet/{sitetype}/{site}/{baselineid}/computers", Method.GET);
                request.AddUrlSegment("sitetype", dbSite.Type);
                request.AddUrlSegment("baselineid", baseline.BaselineID.ToString());
            }

            XDocument response = Execute(request);

            // The returned document should contain 0 or more Computer resource URIs
            if (response.Element("BESAPI").Elements().Count(e => e.Name == "Computer") > 0)
            {
                foreach (XElement computerElement in response.Element("BESAPI").Elements("Computer"))
                {
                    // We only need the last part of the resource URI -- the ID
                    Uri resourceUri = new Uri(computerElement.Attribute("Resource").Value.ToString());
                    results.Add(new BaselineResult(baseline.BaselineID, Int32.Parse(resourceUri.Segments.Last())));
                }
            }

            return results;
        }

        public List<Computer> GetComputers()
        {
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            RestRequest request = new RestRequest("computers", Method.GET);

            List<Computer> computers = Execute<List<Computer>>(request);

            foreach (Computer computer in computers)
            {
                request = new RestRequest("computer/{id}", Method.GET);
                request.AddUrlSegment("id", computer.ComputerID.ToString());

                XDocument response = Execute(request);
                string hostName = response.Element("BESAPI").Element("Computer").Elements("Property")
                    .Where(e => e.Attribute("Name").Value.ToString() == "Computer Name").Single().Value.ToString();
                computer.ComputerName = hostName;
            }

            return computers;
        }

        public List<ComputerGroup> GetComputerGroups()
        {
            List<ComputerGroup> groups = new List<ComputerGroup>();
            BesDb besDb = new BesDb(ConfigurationManager.ConnectionStrings["DB"].ToString());

            foreach (Site dbSite in besDb.Connection.Query<Site>("SELECT * FROM [BESEXT].[SITE]"))
            {
                groups.AddRange(GetComputerGroups(dbSite));
            }

            return groups;
        }

        public List<ComputerGroup> GetComputerGroups(Site site)
        {
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            RestRequest request = new RestRequest("computergroups/{sitetype}/{site}", Method.GET);
            request.AddUrlSegment("sitetype", site.Type);
            request.AddUrlSegment("site", site.Name);

            // TODO: Handle master action site properly
            if (site.Type == "master")
            {
                request = new RestRequest("computergroups/{sitetype}", Method.GET);
                request.AddUrlSegment("sitetype", site.Type);
            }

            List<ComputerGroup> groups = Execute<List<ComputerGroup>>(request);

            // The API does not assign an ID to the Site. Therefore, we use the ID assigned by the DB.
            // Let's fetch the Site from the DB first
            BesDb besDb = new BesDb(ConfigurationManager.ConnectionStrings["DB"].ToString());
            Site dbSite = besDb.SelectSite(site.Name);

            // Assign SiteID if the corresponding Site was found in the DB
            foreach (ComputerGroup group in groups)
            {
                if (dbSite != null)
                {
                    group.SiteID = dbSite.ID;
                }

                // Check if it's a manual group
                group.Manual = IsManualGroup(group);
            }

            return groups;
        }

        public List<ComputerGroupMember> GetGroupMembers(List<ComputerGroup> groups)
        {
            List<ComputerGroupMember> members = new List<ComputerGroupMember>();

            foreach (ComputerGroup group in groups)
            {
                members.AddRange(GetGroupMembers(group));
            }

            return members;
        }

        public List<ComputerGroupMember> GetGroupMembers(ComputerGroup group)
        {
            List<ComputerGroupMember> members = new List<ComputerGroupMember>();

            BesDb besDb = new BesDb(ConfigurationManager.ConnectionStrings["DB"].ToString());
            Site dbSite = besDb.SelectSite(group.SiteID);

            if (dbSite != null)
            {
                if (group.Manual)
                {
                    // If it's a manual group, we need to collect the group members using Relevance FOR SOME REASON
                    // We'll use the following Relevance query:
                    // {0}: The ID of the Computer Group
                    string relevance = "((id of it, name of it) of members of it) of BES Computer Group whose (id of it = {0})";

                    // Let's compose the request string
                    RestRequest request = new RestRequest("query", Method.GET);
                    request.AddQueryParameter("relevance", String.Format(relevance, group.GroupID));

                    XDocument response = Execute(request);

                    // Let's check if the Result element is empty
                    if (response.Element("BESAPI").Element("Query").Element("Result").Elements().Count() > 0)
                    {
                        // All answers are wrapped inside a "Tuple" element
                        foreach (XElement tupleElement in response.Element("BESAPI").Element("Query").Element("Result").Elements("Tuple"))
                        {
                            // The Result consists of two parts:
                            //  1) The ComputerID
                            //  2) The ComputerName (name for debug purposes)
                            XElement computerElement = tupleElement.Elements("Answer").First();
                            members.Add(new ComputerGroupMember(group.GroupID, Convert.ToInt32(computerElement.Value)));
                        }
                    }
                }
                else
                {
                    RestClient client = new RestClient(this.BaseURL);
                    client.Authenticator = this.Authenticator;

                    RestRequest request = new RestRequest("computergroup/{sitetype}/{site}/{id}/computers", Method.GET);
                    request.AddUrlSegment("sitetype", dbSite.Type);
                    request.AddUrlSegment("site", dbSite.Name);
                    request.AddUrlSegment("id", group.GroupID.ToString());

                    XDocument response = Execute(request);

                    if (response.Element("BESAPI").Elements("Computer") != null)
                    {
                        foreach (XElement computerElement in response.Element("BESAPI").Elements("Computer"))
                        {
                            Uri resourceUri = new Uri(computerElement.Attribute("Resource").Value.ToString());
                            members.Add(new ComputerGroupMember(group.GroupID, Int32.Parse(resourceUri.Segments.Last())));
                        }
                    }
                }              
            }

            return members;
        }

        public List<Site> GetSites()
        {
            List<Site> sites = new List<Site>();

            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            RestRequest request = new RestRequest("sites", Method.GET);

            // Execute the request
            XDocument response = Execute(request);

            // TODO: Handle spaces in URLs correctly
            // TODO: Caps/NoCaps nonsense
            foreach (XElement siteElement in response.Element("BESAPI").Elements())
            {
                if (siteElement.Name.ToString() == "ActionSite")
                {
                    sites.Add(new Site(siteElement.Element("Name").Value.ToString(), "master"));
                }
                else
                {
                    sites.Add(new Site(siteElement.Element("Name").Value.ToString(), siteElement.Name.ToString().Replace("Site", "").ToLower()));
                }
                
            }

            return sites;
        }

        private bool IsManualGroup(int groupid)
        {
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            List<AnalysisPropertyResult> results = new List<AnalysisPropertyResult>();

            // We need to use Session Relevance to check if a group is manual or not
            // We'll use the following Relevance query:
            // {0}: The ID of the Computer Group
            string relevance = "(manual flag of it) of BES computer groups whose (id of it = {0})";

            // Let's compose the request string
            RestRequest request = new RestRequest("query", Method.GET);
            request.AddQueryParameter("relevance", String.Format(relevance, groupid));

            XDocument response = Execute(request);

            // Let's check if the Result element is empty
            if (response.Element("BESAPI").Element("Query").Element("Result").Elements().Count() > 0)
            {
                return Convert.ToBoolean(response.Element("BESAPI").Element("Query").Element("Result").Element("Answer").Value);
            }
            return false;
        }

        private bool IsManualGroup(ComputerGroup group)
        {
            RestClient client = new RestClient(this.BaseURL);
            client.Authenticator = this.Authenticator;

            List<AnalysisPropertyResult> results = new List<AnalysisPropertyResult>();

            // We need to use Session Relevance to check if a group is manual or not
            // We'll use the following Relevance query:
            // {0}: The ID of the Computer Group
            string relevance = "(manual flag of it) of BES computer groups whose (id of it = {0})";

            // Let's compose the request string
            RestRequest request = new RestRequest("query", Method.GET);
            request.AddQueryParameter("relevance", String.Format(relevance, group.GroupID));

            XDocument response = Execute(request);

            // Let's check if the Result element is empty
            if (response.Element("BESAPI").Element("Query").Element("Result").Elements().Count() > 0)
            {
                return Convert.ToBoolean(response.Element("BESAPI").Element("Query").Element("Result").Element("Answer").Value);
            }
            return false;
        }

        public XDocument Execute(RestRequest request)
        {
            RestClient client = new RestClient();
            client.BaseUrl = this.BaseURL;
            client.Authenticator = this.Authenticator;

            IRestResponse response = client.Execute(request);

            try
            {
                if (response.ErrorException != null)
                {
                    logger.ErrorException(response.ErrorException.Message, response.ErrorException);
                    throw new Exception(response.ErrorMessage);
                }

                // Return non-deserialized XML document
                return XDocument.Parse(response.Content, LoadOptions.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error encountered: {0}", ex.Message);
                return null;
            }
        }

        public T Execute<T>(RestRequest request) where T : new()
        {
            RestClient client = new RestClient();
            client.BaseUrl = this.BaseURL;
            client.Authenticator = this.Authenticator;

            var response = client.Execute<T>(request);

            try
            {
                if (response.ErrorException != null)
                {
                    throw new Exception(response.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error encountered: {0}", ex.Message);
            }

            return response.Data;
        }
    }
}
