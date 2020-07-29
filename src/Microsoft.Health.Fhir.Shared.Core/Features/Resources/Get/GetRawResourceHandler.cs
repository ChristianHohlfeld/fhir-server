﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using MediatR;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Messages.Get;
using Newtonsoft.Json.Linq;

namespace Microsoft.Health.Fhir.Core.Features.Resources.Get
{
    public class GetRawResourceHandler : BaseResourceHandler, IRequestHandler<GetRawResourceRequest, GetRawResourceResponse>
    {
        public GetRawResourceHandler(
            IFhirDataStore fhirDataStore,
            Lazy<IConformanceProvider> conformanceProvider,
            IResourceWrapperFactory resourceWrapperFactory,
            ResourceIdProvider resourceIdProvider,
            IFhirAuthorizationService authorizationService)
            : base(fhirDataStore, conformanceProvider, resourceWrapperFactory, resourceIdProvider, authorizationService)
        {
        }

        public async Task<GetRawResourceResponse> Handle(GetRawResourceRequest message, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(message, nameof(message));

            if (await AuthorizationService.CheckAccess(DataActions.Read) != DataActions.Read)
            {
                throw new UnauthorizedFhirActionException();
            }

            var key = message.ResourceKey;

            var currentDoc = await FhirDataStore.GetAsync(key, cancellationToken);

            if (currentDoc == null)
            {
                if (string.IsNullOrEmpty(key.VersionId))
                {
                    throw new ResourceNotFoundException(string.Format(Core.Resources.ResourceNotFoundById, key.ResourceType, key.Id));
                }
                else
                {
                    throw new ResourceNotFoundException(string.Format(Core.Resources.ResourceNotFoundByIdAndVersion, key.ResourceType, key.Id, key.VersionId));
                }
            }

            if (currentDoc.IsHistory &&
                ConformanceProvider != null &&
                await ConformanceProvider.Value.CanReadHistory(key.ResourceType, cancellationToken) == false)
            {
                throw new MethodNotAllowedException(string.Format(Core.Resources.ReadHistoryDisabled, key.ResourceType));
            }

            if (currentDoc.IsDeleted)
            {
                // As per FHIR Spec if the resource was marked as deleted on that version or the latest is marked as deleted then
                // we need to return a resource gone message.
                throw new ResourceGoneException(new ResourceKey(currentDoc.ResourceTypeName, currentDoc.ResourceId, currentDoc.Version));
            }

            var raw = JObject.Parse(currentDoc.RawResource.Data);

            JObject meta = (JObject)raw.GetValue("meta", StringComparison.OrdinalIgnoreCase);

            bool hadValues = meta != null;

            if (!hadValues)
            {
                meta = new JObject();
            }

            meta.Add(new JProperty("versionId", currentDoc.Version));
            meta.Add(new JProperty("lastUpdated", currentDoc.LastModified));

            if (!hadValues)
            {
                raw.Add("meta", meta);
                currentDoc.RawResource = new RawResource(raw.ToString(), currentDoc.RawResource.Format, false, false);
            }
            else
            {
                currentDoc.RawResource = new RawResource(raw.ToString(), currentDoc.RawResource.Format, true, true);
            }

            return new GetRawResourceResponse(currentDoc);
        }
    }
}
