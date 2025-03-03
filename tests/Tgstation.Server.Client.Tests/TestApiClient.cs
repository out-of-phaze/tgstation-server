﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Tgstation.Server.Api;
using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Response;

namespace Tgstation.Server.Client.Tests
{
	[TestClass]
	public sealed class TestApiClient
	{
		[TestMethod]
		public async Task TestDeserializingByondModelsWork()
		{
			var sample = new ByondResponse
			{
				Version = new Version(511, 1385, 0)
			};

			var sampleJson = JsonConvert.SerializeObject(sample, new JsonSerializerSettings
			{
				ContractResolver = new CamelCasePropertyNamesContractResolver(),
				Converters = new[] { new VersionConverter() }
			});

			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(sampleJson)
			};

			var httpClient = new Mock<IHttpClient>();
			httpClient.Setup(x => x.SendAsync(It.IsNotNull<HttpRequestMessage>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult(response));

			var client = new ApiClient(httpClient.Object, new Uri("http://fake.com"), new ApiHeaders(new ProductHeaderValue("fake"), "fake"), null, false);

			var result = await client.Read<ByondResponse>(Routes.Byond, default).ConfigureAwait(false);
			Assert.AreEqual(sample.Version, result.Version);
			Assert.AreEqual(0, result.Version.Build);
		}

		[TestMethod]
		public async Task TestUnrecognizedResponse()
		{
			var sample = new ByondResponse
			{
				Version = new Version(511, 1385)
			};

			var fakeJson = "asdfasd <>F#(*)U*#JLI";

			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(fakeJson)
			};

			var httpClient = new Mock<IHttpClient>();
			httpClient.Setup(x => x.SendAsync(It.IsNotNull<HttpRequestMessage>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult(response));

			var client = new ApiClient(httpClient.Object, new Uri("http://fake.com"), new ApiHeaders(new ProductHeaderValue("fake"), "fake"), null, true);

			await Assert.ThrowsExceptionAsync<UnrecognizedResponseException>(() => client.Read<ByondResponse>(Routes.Byond, default)).ConfigureAwait(false);
		}
	}
}
