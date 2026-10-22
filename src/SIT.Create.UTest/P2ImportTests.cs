// SPDX-FileCopyrightText: 2026 Siemens AG
// SPDX-License-Identifier: MIT

using CycloneDX.Models;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SIT.APICommunications.Model;
using SIT.Common;
using SIT.Common.Constants;
using SIT.Common.Model;
using SIT.Facade.Interfaces;
using SIT.Services;
using SIT.Services.Interface;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using File = System.IO.File;

namespace SIT.Create.UTest
{
    /// <summary>Checks P2 identity and binary provenance across the import and SW360 boundary.</summary>
    public class P2ImportTests
    {
        private const string Artifact = "https://download.eclipse.org/releases/2024-09/202409111000/features/org.eclipse.ecf.core.feature_1.6.2.v20240812-1535.jar";
        private const string Identity = "pkg:p2/org.eclipse.ecf.core.feature@1.6.2.v20240812-1535";

        /// <summary>Qualifier metadata must survive Create without becoming a source attachment.</summary>
        [TestCase("repository_url", true, Artifact)]
        [TestCase("url", true, Artifact)]
        [TestCase("download_url", true, Artifact)]
        [TestCase("repository_url", false, Artifact)]
        [TestCase("repository_url", true, "file:///local/feature.jar")]
        [TestCase("repository_url", true, "relative/feature.jar")]
        [TestCase("repository_url", true, null)]
        [TestCase("repository_url", true, Artifact, true)]
        [TestCase("url", true, Artifact, true)]
        [TestCase("download_url", true, Artifact, true)]
        [TestCase("repository_url", true, Artifact, false, true)]
        public async Task P2Import_PreservesIdentityAndDistributionThroughReleasePayload(string qualifier, bool distribution, string artifactUrl, bool explicitProjectType = false, bool mixedReferences = false)
        {
            string purl = Identity + "?classifier=org.eclipse.update.feature&" + qualifier + "=https://download.eclipse.org/releases/2024-09/202409111000";
            var component = new Component
            {
                Type = Component.Classification.Library,
                Name = "org.eclipse.ecf.core.feature",
                Version = "1.6.2.v20240812-1535",
                Purl = purl,
                BomRef = "eclipse-feature",
                Properties = explicitProjectType ? new List<Property> { new Property { Name = Dataconstant.Cdx_ProjectType, Value = "P2" } } : null,
                ExternalReferences = new List<ExternalReference>
                {
                    new ExternalReference { Type = distribution ? ExternalReference.ExternalReferenceType.Distribution : ExternalReference.ExternalReferenceType.Website, Url = artifactUrl }
                }
            };
            if (mixedReferences)
            {
                // Skip an invalid distribution and a website, then choose the first usable distribution.
                component.ExternalReferences.Insert(0, new ExternalReference { Type = ExternalReference.ExternalReferenceType.Distribution, Url = "relative/feature.jar" });
                component.ExternalReferences.Insert(1, new ExternalReference { Type = ExternalReference.ExternalReferenceType.Website, Url = "https://example.com/project" });
                component.ExternalReferences.Add(new ExternalReference { Type = ExternalReference.ExternalReferenceType.Distribution, Url = "https://example.com/alternate.jar" });
            }
            string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "P2_" + FileConstant.BomFileName);
            try
            {
                File.WriteAllText(path, CycloneDX.Json.Serializer.Serialize(new Bom { Components = new List<Component> { component } }));
                var service = new Mock<ISW360Service>();
                service.Setup(x => x.GetAvailableReleasesInSw360(It.IsAny<List<Components>>())).ReturnsAsync(new List<Components>());
                var helper = new CreatorHelper(new Dictionary<string, SIT.Create.Interfaces.IPackageDownloader>());
                var settings = new CommonAppSettings
                {
                    Directory = new SIT.Common.Directory { OutputFolder = directory },
                    SW360 = new SW360 { ProjectName = "P2" }
                };
                var creator = new ComponentCreator();
                var result = await creator.CycloneDxBomParser(settings, service.Object, new CycloneDXBomParser(), helper);
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].ReleaseExternalId, Is.EqualTo(purl));
                Assert.That(result[0].ComponentExternalId, Is.EqualTo("pkg:p2/org.eclipse.ecf.core.feature"));
                string expectedBinaryUrl = distribution && artifactUrl == Artifact ? Artifact : null;
                Assert.That(result[0].PackageUrl, Is.EqualTo(expectedBinaryUrl));
                Assert.That(result[0].DownloadUrl, Is.EqualTo(Dataconstant.DownloadUrlNotFound));
                Assert.That(await helper.DownloadReleaseAttachmentSource(result[0]), Is.Empty);

                Releases payload = null;
                var facade = new Mock<ISW360ApicommunicationFacade>();
                facade.Setup(x => x.CreateRelease(It.IsAny<Releases>()))
                    .Callback<Releases>(release => payload = release)
                    .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Created));
                await new Sw360CreatorService(facade.Object).CreateReleaseForComponent(result[0], "component-id", new Dictionary<string, string>());
                Assert.That(payload.ExternalIds.Package_Url, Is.EqualTo(purl));
                Assert.That(payload.BinaryDownloadUrl, Is.EqualTo(expectedBinaryUrl ?? string.Empty));
                Assert.That(payload.SourceDownloadurl, Is.Empty);

                // Inspect the same retained BOM that Create passes to its export serializer.
                var field = typeof(ComponentCreator).GetField("bom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var exported = CycloneDX.Json.Serializer.Deserialize(CycloneDX.Json.Serializer.Serialize((Bom)field.GetValue(creator)));
                Assert.That(exported.Components[0].Purl, Is.EqualTo(purl));
                Assert.That(exported.Components[0].BomRef, Is.EqualTo("eclipse-feature"));
                Assert.That(exported.Components[0].ExternalReferences.Select(reference => reference.Url),
                    Is.EqualTo(component.ExternalReferences.Select(reference => reference.Url)));
            }
            finally
            {
                File.Delete(path);
                System.IO.Directory.Delete(directory);
            }
        }

        /// <summary>Identity evidence is retained metadata, while invented alias fields are not part of the model.</summary>
        [Test]
        public void P2Parser_RetainsEvidenceButDoesNotRecognizePurlAliases()
        {
            string path = Path.GetTempFileName();
            try
            {
                var input = JObject.Parse("{\"bomFormat\":\"CycloneDX\",\"specVersion\":\"1.6\",\"version\":1,\"components\":[{\"type\":\"library\",\"name\":\"feature\",\"version\":\"1\"}]}");
                var component = input["components"][0];
                component["purl"] = Identity;
                component["purls"] = new JArray(Identity, "pkg:generic/feature@1");
                component["evidence"] = JObject.Parse("{\"identity\":[{\"field\":\"purl\",\"concludedValue\":\"pkg:generic/feature@1\",\"confidence\":0.5}]}");
                File.WriteAllText(path, input.ToString());
                var bom = new CycloneDXBomParser().ParseCycloneDXBom(path);
                var output = JObject.Parse(CycloneDX.Json.Serializer.Serialize(bom));
                Assert.That(output["components"][0]["purl"].ToString(), Is.EqualTo(Identity));
                Assert.That(output["components"][0]["purls"], Is.Null);
                var evidence = output["components"][0]["evidence"]["identity"];
                Assert.That((evidence is JArray ? evidence[0] : evidence)["concludedValue"].ToString(), Is.EqualTo("pkg:generic/feature@1"));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
