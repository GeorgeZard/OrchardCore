using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Metadata.Models;
using OrchardCore.ContentManagement.Metadata.Settings;
using OrchardCore.ContentManagement.Records;
using OrchardCore.Contents.ViewModels;
using OrchardCore.DisplayManagement.ModelBinding;
using YesSql;
using YesSql.Services;

namespace OrchardCore.Contents.Services
{
    public class DefaultContentsAdminListFilter : IContentsAdminListFilter
    {
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly IAuthorizationService _authorizationService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IContentManager _contentManager;

        public DefaultContentsAdminListFilter(
            IContentDefinitionManager contentDefinitionManager,
            IAuthorizationService authorizationService,
            IHttpContextAccessor httpContextAccessor,
            IContentManager contentManager)
        {
            _contentDefinitionManager = contentDefinitionManager;
            _authorizationService = authorizationService;
            _httpContextAccessor = httpContextAccessor;
            _contentManager = contentManager;
        }

        public async Task FilterAsync(ContentOptionsViewModel model, IQuery<ContentItem> query, IUpdateModel updater)
        {
            var user = _httpContextAccessor.HttpContext.User;

            if (!String.IsNullOrEmpty(model.DisplayText))
            {
                query.With<ContentItemIndex>(x => x.DisplayText.Contains(model.DisplayText));
            }

            switch (model.ContentsStatus)
            {
                case ContentsStatus.Published:
                    query.With<ContentItemIndex>(x => x.Published);
                    break;
                case ContentsStatus.Draft:
                    query.With<ContentItemIndex>(x => x.Latest && !x.Published);
                    break;
                case ContentsStatus.AllVersions:
                    query.With<ContentItemIndex>(x => x.Latest);
                    break;
                default:
                    query.With<ContentItemIndex>(x => x.Latest);
                    break;
            }

            // Filter the creatable types.
            if (!string.IsNullOrEmpty(model.SelectedContentType))
            {
                var contentTypeDefinition = _contentDefinitionManager.GetTypeDefinition(model.SelectedContentType);
                if (contentTypeDefinition != null)
                {
                    // We display a specific type even if it's not listable so that admin pages
                    // can reuse the Content list page for specific types.
                    query.With<ContentItemIndex>(x => x.ContentType == model.SelectedContentType);
                }
            }
            else
            {
                var listableTypes = new List<ContentTypeDefinition>();
                foreach (var ctd in _contentDefinitionManager.ListTypeDefinitions())
                {
                    if (ctd.GetSettings<ContentTypeSettings>().Listable)
                    {
                        // We want to list the content item if the user can edit their own items at least. It might display content items the user won't be able to edit though.
                        var contentItem = await _contentManager.NewAsync(ctd.Name);
                        contentItem.Owner = user.Identity.Name;
                        
                        var authorized = await _authorizationService.AuthorizeAsync(user, CommonPermissions.EditContent, contentItem);
                        if (authorized)
                        {
                            listableTypes.Add(ctd);
                        }
                    }
                }
                
                query.With<ContentItemIndex>(x => x.ContentType.IsIn(listableTypes.Select(t => t.Name).ToArray()));
            }

            // If we set the ViewNotOwnedItemsAdminContentList permission
            // to false we can only view our own content and 
            // we bypass the corresponding ContentsStatus by owned content filtering
            if(!await _authorizationService.AuthorizeAsync(user, Permissions.ViewNotOwnedItemsAdminContentList))
            {
                query.With<ContentItemIndex>(x => x.Owner == user.Identity.Name);
            }
            else
            {
                if (model.ContentsStatus == ContentsStatus.Owner)
                {
                    query.With<ContentItemIndex>(x => x.Owner == user.Identity.Name);
                }
            }
            
            // Apply OrderBy filters.
            switch (model.OrderBy)
            {
                case ContentsOrder.Modified:
                    query.With<ContentItemIndex>().OrderByDescending(x => x.ModifiedUtc);
                    break;
                case ContentsOrder.Published:
                    query.With<ContentItemIndex>().OrderByDescending(cr => cr.PublishedUtc);
                    break;
                case ContentsOrder.Created:
                    query.With<ContentItemIndex>().OrderByDescending(cr => cr.CreatedUtc);
                    break;
                case ContentsOrder.Title:
                    query.With<ContentItemIndex>().OrderBy(cr => cr.DisplayText);
                    break;
                default:
                    query.With<ContentItemIndex>().OrderByDescending(cr => cr.ModifiedUtc);
                    break;
            };
        }
    }
}
