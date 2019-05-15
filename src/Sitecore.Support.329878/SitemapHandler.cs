namespace Sitecore.Support.XA.Feature.SiteMetadata.Pipelines.HttpRequestBegin
{
  using Microsoft.Extensions.DependencyInjection;
  using Sitecore.Configuration;
  using Sitecore.Data;
  using Sitecore.Data.Items;
  using Sitecore.DependencyInjection;
  using Sitecore.Diagnostics;
  using Sitecore.IO;
  using Sitecore.Links;
  using Sitecore.Pipelines.HttpRequest;
  using Sitecore.Web;
  using Sitecore.XA.Feature.SiteMetadata.Enums;
  using Sitecore.XA.Feature.SiteMetadata.Sitemap;
  using Sitecore.XA.Foundation.Abstractions;
  using Sitecore.XA.Foundation.Multisite;
  using Sitecore.XA.Foundation.SitecoreExtensions.Extensions;
  using Sitecore.XA.Foundation.SitecoreExtensions.Utils;
  using System;
  using System.Collections.Specialized;
  using System.IO;
  using System.Text;
  using System.Threading.Tasks;
  using System.Web;
  using System.Web.Caching;
  using Templates = Sitecore.XA.Feature.SiteMetadata.Templates;

  public class SitemapHandler : HttpRequestProcessor
  {
    public SitemapHandler()
    {
      string str = $"sitemap-{this.CurrentSite.Name}.xml";
      this.FilePath = Path.Combine(TempFolder.Folder, str);
    }

    protected virtual SitemapLinkOptions GetLinkBuilderOptions()
    {
      SitemapLinkOptions sitemapOpt = new SitemapLinkOptions(HttpContext.Current.Request.Url, this.ResolveTargetHostName(this.CurrentSite), this.GetUrlOptions());
      if (!string.IsNullOrEmpty(CurrentSite.Scheme))
      {
        sitemapOpt.Scheme = CurrentSite.Scheme;
      }
      return sitemapOpt;
    }
    protected virtual Item GetSettingsItem() =>
        ServiceProviderServiceExtensions.GetService<IMultisiteContext>(ServiceLocator.ServiceProvider).GetSettingsItem(this.Context.Database.GetItem(this.Context.Site.StartPath));

    protected virtual string GetSitemap(Item settings)
    {
      NameValueCollection externalSitemaps = WebUtil.ParseUrlParameters(settings[Sitecore.XA.Feature.SiteMetadata.Templates.Sitemap._SitemapSettings.Fields.ExternalSitemaps]);
      ISitemapGenerator service = ServiceProviderServiceExtensions.GetService<ISitemapGenerator>(ServiceLocator.ServiceProvider);
      var field = settings.Fields[Templates.Sitemap._SitemapSettings.Fields.SitemapIndex];
      if (field.Value == "1")
      {
        return service.BuildSitemapIndex(externalSitemaps);
      }

      string path = this.CurrentSite.RootPath + this.CurrentSite.StartItem;

      Database db = Factory.GetDatabase(this.Context.Database.Name);

      Item item = db.GetItem(path);

      string iSMGen = service.GenerateSitemap(item, externalSitemaps, this.GetLinkBuilderOptions());


      return iSMGen;
    }

    protected virtual string GetSitemapFromCache()
    {
      string str = null;
      if (System.Web.HttpRuntime.Cache[this.CacheKey] != null)
      {
        str = System.Web.HttpRuntime.Cache.Get(this.CacheKey) as string;
      }
      return str;
    }

    protected virtual string GetSitemapFromFile()
    {
      string str = null;
      if (FileUtil.Exists(this.FilePath))
      {
        using (StreamReader reader = new StreamReader(FileUtil.OpenRead(this.FilePath)))
        {
          str = reader.ReadToEnd();
        }
      }
      return str;
    }

    protected virtual UrlOptions GetUrlOptions()
    {
      UrlOptions defaultUrlOptions = LinkManager.GetDefaultUrlOptions();
      defaultUrlOptions.Site = this.Context.Site;
      defaultUrlOptions.AlwaysIncludeServerUrl = false;
      defaultUrlOptions.SiteResolving = true;
      return defaultUrlOptions;
    }

    public override void Process(HttpRequestArgs args)
    {
      Uri url = HttpContext.Current.Request.Url;
      if (url.PathAndQuery.EndsWith("/sitemap.xml", StringComparison.OrdinalIgnoreCase))
      {
        if (this.CurrentSite == null)
        {
          goto TR_0000;
        }
        else if (UrlUtils.IsUrlValidForFile(url, this.CurrentSite, "/sitemap.xml"))
        {
          string sitemap;
          Item settingsItem = this.GetSettingsItem();
          SitemapStatus status = (settingsItem != null) ? settingsItem.Fields[Sitecore.XA.Feature.SiteMetadata.Templates.Sitemap._SitemapSettings.Fields.SitemapMode].ToEnum<SitemapStatus>() : SitemapStatus.Inactive;
          if (status == SitemapStatus.Inactive)
          {
            Log.Info("SitemapHandler (sitemap.xml) : " + $"sitemap is off (status : {status})", this);
            return;
          }
          if (status == SitemapStatus.StoredInCache)
          {
            sitemap = this.GetSitemapFromCache();
            if (string.IsNullOrEmpty(sitemap))
            {
              sitemap = this.GetSitemap(settingsItem);
              this.StoreSitemapInCache(sitemap, this.CacheKey);
            }
          }
          else
          {
            if (status != SitemapStatus.StoredInFile)
            {
              Log.Info("SitemapHandler (sitemap.xml) : unknown error", this);
              return;
            }
            sitemap = this.GetSitemapFromFile();
            if (string.IsNullOrEmpty(sitemap))
            {
              sitemap = this.GetSitemap(settingsItem);
              Task.Factory.StartNew(() => this.SaveSitemapToFile(this.FilePath, sitemap));
            }
          }
          this.SetResponse(args.Context.Response, sitemap);
          args.AbortPipeline();
        }
        else
        {
          goto TR_0000;
        }
      }
      return;
    TR_0000:
      Log.Info("SitemapHandler (sitemap.xml) : " + $"cannot resolve site or url ({url})", this);
    }

    protected virtual string ResolveTargetHostName(SiteInfo currentSite) =>
        (string.IsNullOrEmpty(currentSite.TargetHostName) ? (!currentSite.IsHostNameUnique() ? HttpContext.Current.Request.Url.Host : currentSite.HostName) : currentSite.TargetHostName);

    protected virtual void SaveSitemapToFile(string filePath, string sitemap)
    {
      using (StreamWriter writer = new StreamWriter(FileUtil.OpenCreate(filePath)))
      {
        writer.Write(sitemap);
      }
    }

    protected virtual void SetResponse(HttpResponse response, object content)
    {
      response.ContentType = "application/xml";
      response.ContentEncoding = Encoding.UTF8;
      response.Write(content);
      response.End();
    }

    protected virtual void StoreSitemapInCache(string sitemap, string cacheKey)
    {
      System.Web.HttpRuntime.Cache.Insert(cacheKey, sitemap, null, DateTime.UtcNow.AddMinutes((double)this.CacheExpiration), Cache.NoSlidingExpiration);
    }

    protected SiteInfo CurrentSite =>
        this.Context.Site.SiteInfo;

    protected string FilePath { get; set; }

    protected string CacheKey
    {
      get
      {
        string name;
        Database database = this.Context.Database;
        if (database != null)
        {
          name = database.Name;
        }
        else
        {
          Database local1 = database;
          name = null;
        }

        string scheme = !string.IsNullOrEmpty(CurrentSite.Scheme) ? CurrentSite.Scheme : HttpContext.Current.Request.Url.Scheme;

        return $"{"XA-SITEMAP"}/{name}/{this.CurrentSite.Name}/{HttpContext.Current.Request.Url}/{scheme}";
      }
    }

    public int CacheExpiration { get; set; }

    protected IContext Context { get; } = ServiceProviderServiceExtensions.GetService<IContext>(ServiceLocator.ServiceProvider);
  }
}
