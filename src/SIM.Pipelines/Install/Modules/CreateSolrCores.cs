﻿using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Sitecore.Diagnostics.Base;
using SIM.Instances;
using SIM.Pipelines.InstallModules;
using SIM.Products;

namespace SIM.Pipelines.Install.Modules
{
  using SIM.Extensions;

  /// <summary>
  /// Creates cores for all configured Solr indexes
  /// </summary>
  public class CreateSolrCores : IPackageInstallActions
  {
    #region Constants

    private const string GenerateSchemaClass = "Sitecore.ContentSearch.ProviderSupport.Solr.SchemaGenerator";
    private const string GenerateSchemaMethod = "GenerateSchema";
    public const string SolrConfigPatch =
    @"<config>
        <requestHandler name=""/select"" class=""solr.SearchHandler"">
          <bool name=""terms"">true</bool>
          <lst name=""defaults"">
             <bool name=""terms"">true</bool>
          </lst>
          <arr name=""last-components"">
            <str>terms</str>
          </arr>
        </requestHandler>
      </config>";

    #endregion

    #region Public methods

    public void Execute(Instance instance, Product module)
    {
      XmlDocument config = instance.GetShowconfig();
      string solrUrl = GetUrl(config);
      XmlNodeList solrIndexes = GetSolrIndexNodes(config);
      string defaultCollectionPath = GetDefaultCollectionPath(solrUrl);

      foreach (XmlElement node in solrIndexes)
      {
        string coreName = GetCoreName(node);
        string corePath = defaultCollectionPath.Replace("collection1", coreName);
        this.CopyDirectory(defaultCollectionPath, corePath);
        DeleteCopiedCorePropertiesFile(corePath);
        UpdateSchema(instance.WebRootPath, corePath);
        UpdateSolrConfig(corePath);
        CallSolrCreateCoreAPI(solrUrl, coreName, corePath);
      }
    }

    #endregion

    #region Private methods

    private void UpdateSolrConfig(string corePath)
    {
      string filePath = corePath + @"conf\solrconfig.xml";
      XmlDocumentEx mergedXml = this.XmlMerge(filePath,SolrConfigPatch);
      string mergedString = this.NormalizeXml(mergedXml);
      this.WriteAllText(filePath, mergedString);
    }

    private static XmlNodeList GetSolrIndexNodes(XmlDocument config)
    {
      return config.SelectNodes(
        "/sitecore/contentSearch/configuration/indexes/index[@type='Sitecore.ContentSearch.SolrProvider.SolrSearchIndex, Sitecore.ContentSearch.SolrProvider']");
    }

    private static string GetUrl(XmlDocument config)
    {
      XmlNode serviceBaseAddressNode = config.SelectSingleNode("/sitecore/settings/setting[@name='ContentSearch.Solr.ServiceBaseAddress']");
      serviceBaseAddressNode = Assert.IsNotNull(serviceBaseAddressNode,
        "ContentSearch.Solr.ServiceBaseAddress not found in configuration.");

      XmlAttribute valueAttribute = serviceBaseAddressNode.Attributes["value"];
      Assert.IsNotNull(valueAttribute, "ContentSearch.Solr.ServiceBaseAddress value attribute not found.");
      return valueAttribute.Value;
    }

    private void UpdateSchema(string webRootPath, string corePath)
    {
      string contentSearchDllPath = webRootPath.EnsureEnd(@"\") + @"bin\Sitecore.ContentSearch.dll";
      string schemaPath = corePath.EnsureEnd(@"\") + @"conf\schema.xml";
      string managedSchemaPath = corePath.EnsureEnd(@"\") + @"conf\managed-schema";

      bool schemaExists = FileExists(schemaPath);
      bool managedSchemaExists = FileExists(managedSchemaPath);

      if (!schemaExists && !managedSchemaExists)
      {
        throw new FileNotFoundException($"Schema file not found: Checked here {schemaPath} and here {managedSchemaPath}.");
      }

      string inputPath = managedSchemaExists ? managedSchemaPath : schemaPath;
      string outputPath = schemaPath;
      this.GenerateSchema(contentSearchDllPath, inputPath, outputPath);
    }

    private static string GetCoreName(XmlElement node)
    {
      var coreElement = node.SelectSingleNode("param[@desc='core']") as XmlElement;
      string id = node.Attributes["id"].InnerText;
      coreElement = Assert.IsNotNull(coreElement, "param[@desc='core'] not found in Solr configuration file");
      string coreName = coreElement.InnerText.Replace("$(id)", id);
      return coreName;
    }

    private void CallSolrCreateCoreAPI(string url, string coreName, string instanceDir)
    {
      this.RequestAndGetResponse($"{url}/admin/cores?action=CREATE&name={coreName}&instanceDir={instanceDir}&config=solrconfig.xml&schema=schema.xml&dataDir=data");
    }

    private void DeleteCopiedCorePropertiesFile(string newCorePath)
    {
      string path = string.Format(newCorePath.EnsureEnd(@"\") + "core.properties");
      this.DeleteFile(path);
    }

    private string GetDefaultCollectionPath(string url)
    {
      var response = this.RequestAndGetResponse($"{url}/admin/cores");

      var doc = new XmlDocument();
      doc.Load(response.GetResponseStream());

      XmlNode collection1Node = doc.SelectSingleNode("/response/lst[@name='status']/lst[@name='collection1']");
      if (collection1Node == null) throw new ApplicationException("collection1 not found");

      return collection1Node.SelectSingleNode("str[@name='instanceDir']").InnerText;
    }

    #endregion

    #region Virtual methods
    // All system calls are wrapped in virtual methods for unit testing.

    public virtual HttpWebResponse RequestAndGetResponse(string url)
    {
      return WebRequestHelper.RequestAndGetResponse(url);
    }

    public virtual void CopyDirectory(string sourcePath, string destinationPath)
    {
      FileSystem.FileSystem.Local.Directory.Copy(sourcePath, destinationPath, recursive: true);
    }

    public virtual void WriteAllText(string path, string text)
    {
      FileSystem.FileSystem.Local.File.WriteAllText(path, text);
    }

    public virtual void DeleteFile(string path)
    {
      FileSystem.FileSystem.Local.File.Delete(path);
    }

    public virtual bool FileExists(string path)
    {
      return FileSystem.FileSystem.Local.File.Exists(path);
    }

    /// <summary>
    /// Dynamically loads GenerateSchema class from target site, and uses it
    /// to applied required changes to the Solr schema.xml file.
    /// </summary>
    public virtual void GenerateSchema(string dllPath, string inputPath, string outputPath)
    {
      Assembly assembly = ReflectionUtil.GetAssembly(dllPath);
      Type generateSchema = ReflectionUtil.GetType(assembly, GenerateSchemaClass);
      object obj = ReflectionUtil.CreateObject(generateSchema);
      ReflectionUtil.InvokeMethod(obj, GenerateSchemaMethod, inputPath, outputPath);
    }
   
    public virtual XmlDocumentEx XmlMerge(string filePath, string xmlString)
    {
      XmlDocumentEx doc1 = XmlDocumentEx.LoadFile(filePath);
      XmlDocument doc2 = XmlDocumentEx.LoadXml(xmlString);
      return doc1.Merge(doc2); 
    }

    public virtual string NormalizeXml(XmlDocumentEx xml)
    {
      string outerXml = xml.OuterXml;
      string formatted = XmlDocumentEx.Normalize(outerXml);
      Regex r = new Regex(@"^<\?.*\?>");
      string corrected = r.Replace(formatted, @"<?xml version=""1.0"" encoding=""UTF-8"" ?>");  //Solr requires UTF-8.
      return corrected;
    }

    #endregion
  }
}
