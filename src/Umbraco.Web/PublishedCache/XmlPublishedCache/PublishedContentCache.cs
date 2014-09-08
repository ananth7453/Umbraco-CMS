using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.XPath;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Models;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Xml;
using Umbraco.Web.Routing;
using System.Linq;
using GlobalSettings = umbraco.GlobalSettings;

namespace Umbraco.Web.PublishedCache.XmlPublishedCache
{
    internal class PublishedContentCache : PublishedCacheBase, IPublishedContentCache
    {
        public PublishedContentCache(XmlStore xmlStore, ICacheProvider cacheProvider, RoutesCache routesCache, string previewToken)
            : base(previewToken.IsNullOrWhiteSpace() == false)
        {
            _xmlStore = xmlStore;
            _cacheProvider = cacheProvider;
            _routesCache = routesCache; // may be null for unit-testing

            if (previewToken.IsNullOrWhiteSpace() == false)
                _previewContent = new PreviewContent(previewToken);
        }

        private readonly ICacheProvider _cacheProvider;
        private readonly RoutesCache _routesCache;

        // for unit tests
        internal RoutesCache RoutesCache { get { return _routesCache; } }

        #region Routes

        public virtual IPublishedContent GetByRoute(bool preview, string route, bool? hideTopLevelNode = null)
        {
            if (route == null) throw new ArgumentNullException("route");

            // try to get from cache if not previewing
            var contentId = (preview || _routesCache == null) ? 0 : _routesCache.GetNodeId(route);

            // if found id in cache then get corresponding content
            // and clear cache if not found - for whatever reason
            IPublishedContent content = null;
            if (contentId > 0)
            {
                content = GetById(preview, contentId);
                if (content == null && _routesCache != null)
                    _routesCache.ClearNode(contentId);
            }

            // still have nothing? actually determine the id
            hideTopLevelNode = hideTopLevelNode ?? GlobalSettings.HideTopLevelNodeFromPath; // default = settings
            content = content ?? DetermineIdByRoute(preview, route, hideTopLevelNode.Value);

            // cache if we have a content and not previewing
            if (content != null && preview == false && _routesCache != null)
            {
                var domainRootNodeId = route.StartsWith("/") ? -1 : int.Parse(route.Substring(0, route.IndexOf('/')));
                var iscanon = DomainHelper.ExistsDomainInPath(DomainHelper.GetAllDomains(false), content.Path, domainRootNodeId) == false;
                // and only if this is the canonical url (the one GetUrl would return)
                if (iscanon)
                    _routesCache.Store(contentId, route);
            }

            return content;
        }

        public IPublishedContent GetByRoute(string route, bool? hideTopLevelNode = null)
        {
            return GetByRoute(CurrentPreview, route, hideTopLevelNode);
        }

        public virtual string GetRouteById(bool preview, int contentId)
        {
            // try to get from cache if not previewing
            var route = (preview || _routesCache == null) ? null : _routesCache.GetRoute(contentId);

            // if found in cache then return
            if (route != null)
                return route;

            // else actually determine the route
            route = DetermineRouteById(preview, contentId);

            // cache if we have a route and not previewing
            if (route != null && preview == false && _routesCache != null)
                _routesCache.Store(contentId, route);

            return route;
        }

        public string GetRouteById(int contentId)
        {
            return GetRouteById(CurrentPreview, contentId);
        }

        IPublishedContent DetermineIdByRoute(bool preview, string route, bool hideTopLevelNode)
        {
            if (route == null) throw new ArgumentNullException("route");

            //the route always needs to be lower case because we only store the urlName attribute in lower case
            route = route.ToLowerInvariant();

            var pos = route.IndexOf('/');
            var path = pos == 0 ? route : route.Substring(pos);
            var startNodeId = pos == 0 ? 0 : int.Parse(route.Substring(0, pos));
            IEnumerable<XPathVariable> vars;

            var xpath = CreateXpathQuery(startNodeId, path, hideTopLevelNode, out vars);

            //check if we can find the node in our xml cache
            var content = GetSingleByXPath(preview, xpath, vars == null ? null : vars.ToArray());

            // if hideTopLevelNodePath is true then for url /foo we looked for /*/foo
            // but maybe that was the url of a non-default top-level node, so we also
            // have to look for /foo (see note in ApplyHideTopLevelNodeFromPath).
            if (content == null && hideTopLevelNode && path.Length > 1 && path.IndexOf('/', 1) < 0)
            {
                xpath = CreateXpathQuery(startNodeId, path, false, out vars);
                content = GetSingleByXPath(preview, xpath, vars == null ? null : vars.ToArray());
            }

            return content;
        }

        string DetermineRouteById(bool preview, int contentId)
        {
            var node = GetById(preview, contentId);
            if (node == null)
                return null;

            // walk up from that node until we hit a node with a domain,
            // or we reach the content root, collecting urls in the way
            var pathParts = new List<string>();
            var n = node;
            var hasDomains = DomainHelper.NodeHasDomains(n.Id);
            while (hasDomains == false && n != null) // n is null at root
            {
                // get the url
                var urlName = n.UrlName;
                pathParts.Add(urlName);

                // move to parent node
                n = n.Parent;
                hasDomains = n != null && DomainHelper.NodeHasDomains(n.Id);
            }

            // no domain, respect HideTopLevelNodeFromPath for legacy purposes
            if (hasDomains == false && GlobalSettings.HideTopLevelNodeFromPath)
                ApplyHideTopLevelNodeFromPath(node, pathParts, preview);

            // assemble the route
            pathParts.Reverse();
            var path = "/" + string.Join("/", pathParts); // will be "/" or "/foo" or "/foo/bar" etc
            var route = (n == null ? "" : n.Id.ToString(CultureInfo.InvariantCulture)) + path;

            return route;
        }

        void ApplyHideTopLevelNodeFromPath(IPublishedContent content, IList<string> segments, bool preview)
        {
            // in theory if hideTopLevelNodeFromPath is true, then there should be only once
            // top-level node, or else domains should be assigned. but for backward compatibility
            // we add this check - we look for the document matching "/" and if it's not us, then
            // we do not hide the top level path
            // it has to be taken care of in GetByRoute too so if
            // "/foo" fails (looking for "/*/foo") we try also "/foo". 
            // this does not make much sense anyway esp. if both "/foo/" and "/bar/foo" exist, but
            // that's the way it works pre-4.10 and we try to be backward compat for the time being
            if (content.Parent == null)
            {
                var rootNode = GetByRoute(preview, "/", true);
                if (rootNode == null)
                    throw new Exception("Failed to get node at /.");
                if (rootNode.Id == content.Id) // remove only if we're the default node
                    segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.RemoveAt(segments.Count - 1);
            }
        }

        #endregion

        #region Converters

        private static IPublishedContent ConvertToDocument(XmlNode xmlNode, bool isPreviewing, ICacheProvider cacheProvider)
		{
		    return xmlNode == null 
                ? null
                : (new XmlPublishedContent(xmlNode, isPreviewing, cacheProvider)).CreateModel();
		}

        private static IEnumerable<IPublishedContent> ConvertToDocuments(XmlNodeList xmlNodes, bool isPreviewing, ICacheProvider cacheProvider)
        {
            return xmlNodes.Cast<XmlNode>()
                .Select(xmlNode => (new XmlPublishedContent(xmlNode, isPreviewing, cacheProvider)).CreateModel());
        }

        #endregion

        #region Getters

        public override IPublishedContent GetById(bool preview, int nodeId)
    	{
    		return ConvertToDocument(GetXml(preview).GetElementById(nodeId.ToString(CultureInfo.InvariantCulture)), preview, _cacheProvider);
    	}

        public override bool HasById(bool preview, int contentId)
        {
            return GetXml(preview).CreateNavigator().MoveToId(contentId.ToString(CultureInfo.InvariantCulture));
        }

        public override IEnumerable<IPublishedContent> GetAtRoot(bool preview)
        {
            return ConvertToDocuments(GetXml(preview).SelectNodes(XPathStrings.RootDocuments), preview, _cacheProvider);
		}

        public override IPublishedContent GetSingleByXPath(bool preview, string xpath, params XPathVariable[] vars)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");
            if (string.IsNullOrWhiteSpace(xpath)) return null;

            var xml = GetXml(preview);
            var node = vars == null
                ? xml.SelectSingleNode(xpath)
                : xml.SelectSingleNode(xpath, vars);
            return ConvertToDocument(node, preview, _cacheProvider);
        }

        public override IPublishedContent GetSingleByXPath(bool preview, XPathExpression xpath, params XPathVariable[] vars)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");

            var xml = GetXml(preview);
            var node = vars == null
                ? xml.SelectSingleNode(xpath)
                : xml.SelectSingleNode(xpath, vars);
            return ConvertToDocument(node, preview, _cacheProvider);
        }

        public override IEnumerable<IPublishedContent> GetByXPath(bool preview, string xpath, params XPathVariable[] vars)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");
            if (string.IsNullOrWhiteSpace(xpath)) return Enumerable.Empty<IPublishedContent>();

            var xml = GetXml(preview);
            var nodes = vars == null
                ? xml.SelectNodes(xpath)
                : xml.SelectNodes(xpath, vars);
            return ConvertToDocuments(nodes, preview, _cacheProvider);
        }

        public override IEnumerable<IPublishedContent> GetByXPath(bool preview, XPathExpression xpath, params XPathVariable[] vars)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");

            var xml = GetXml(preview);
            var nodes = vars == null
                ? xml.SelectNodes(xpath)
                : xml.SelectNodes(xpath, vars);
            return ConvertToDocuments(nodes, preview, _cacheProvider);
        }

        public override bool HasContent(bool preview)
        {
	        var xml = GetXml(preview);
			if (xml == null)
				return false;
			var node = xml.SelectSingleNode(XPathStrings.RootDocuments);
			return node != null;
        }

        public override XPathNavigator CreateNavigator(bool preview)
        {
            var xml = GetXml(preview);
            return xml.CreateNavigator();
        }

        #endregion

        #region Legacy Xml

        private readonly XmlStore _xmlStore;
        private readonly PreviewContent _previewContent;

        internal XmlDocument GetXml(bool preview)
        {
            // not trying to be thread-safe here, that's not the point

            if (preview)
            {
                // Xml cache does not support retrieving preview content when not previewing
                // fixme - should it be an exception or a transparent fallback to XmlStore?
                if (_previewContent == null)
                    throw new InvalidOperationException("Cannot retrieve preview content when not previewing.");

                // PreviewContent tries to load the Xml once and if it fails,
                // it invalidates itself and always return null for XmlContent.
                var previewXml = _previewContent.XmlContent;
                if (previewXml != null)
                    return previewXml;
            }

            return _xmlStore.GetXml();
        }

        #endregion

        #region XPathQuery

        static readonly char[] SlashChar = { '/' };

        protected string CreateXpathQuery(int startNodeId, string path, bool hideTopLevelNodeFromPath, out IEnumerable<XPathVariable> vars)
        {
            string xpath;
            vars = null;

            if (path == string.Empty || path == "/")
            {
                // if url is empty
                if (startNodeId > 0)
                {
					// if in a domain then use the root node of the domain
					xpath = string.Format(XPathStrings.Root + XPathStrings.DescendantDocumentById, startNodeId);                    
                }
                else
                {
                    // if not in a domain - what is the default page?
                    // let's say it is the first one in the tree, if any -- order by sortOrder

					// but!
					// umbraco does not consistently guarantee that sortOrder starts with 0
					// so the one that we want is the one with the smallest sortOrder
					// read http://stackoverflow.com/questions/1128745/how-can-i-use-xpath-to-find-the-minimum-value-of-an-attribute-in-a-set-of-elemen
                    
					// so that one does not work, because min(@sortOrder) maybe 1
					// xpath = "/root/*[@isDoc and @sortOrder='0']";

					// and we can't use min() because that's XPath 2.0
					// that one works
					xpath = XPathStrings.RootDocumentWithLowestSortOrder;
                }
            }
            else
            {
                // if url is not empty, then use it to try lookup a matching page
                var urlParts = path.Split(SlashChar, StringSplitOptions.RemoveEmptyEntries);
                var xpathBuilder = new StringBuilder();
                int partsIndex = 0;
                List<XPathVariable> varsList = null;

                if (startNodeId == 0)
                {
                    // if hiding, first node is not in the url
                    xpathBuilder.Append(hideTopLevelNodeFromPath ? XPathStrings.RootDocuments : XPathStrings.Root);
                }
                else
                {
					xpathBuilder.AppendFormat(XPathStrings.Root + XPathStrings.DescendantDocumentById, startNodeId);
					// always "hide top level" when there's a domain
                }

                while (partsIndex < urlParts.Length)
                {
                    var part = urlParts[partsIndex++];
                    if (part.Contains('\'') || part.Contains('"'))
                    {
                        // use vars, escaping gets ugly pretty quickly
                        varsList = varsList ?? new List<XPathVariable>();
                        var varName = string.Format("var{0}", partsIndex);
                        varsList.Add(new XPathVariable(varName, part));
                        xpathBuilder.AppendFormat(XPathStrings.ChildDocumentByUrlNameVar, varName);
                    }
                    else
                    {
                        xpathBuilder.AppendFormat(XPathStrings.ChildDocumentByUrlName, part);
                        
                    }
                }

                xpath = xpathBuilder.ToString();
                if (varsList != null)
                    vars = varsList.ToArray();
            }

            return xpath;
        }

        #endregion

        #region Detached

        public IPublishedProperty CreateDetachedProperty(PublishedPropertyType propertyType, object value, bool isPreviewing)
        {
            if (propertyType.IsDetachedOrNested == false)
                throw new ArgumentException("Property type is neither detached nor nested.", "propertyType");
            return new XmlPublishedProperty(propertyType, isPreviewing, value.ToString());
        }

        #endregion
    }
}