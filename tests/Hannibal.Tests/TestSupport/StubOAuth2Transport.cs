using System.Net;
using NSubstitute;
using OAuth2.Infrastructure;
using RestSharp;

namespace Hannibal.Tests.TestSupport;

/// <summary>
/// An offline RestSharp transport for the OAuth2 clients.
///
/// Follows the submodule's own NSubstitute precedent
/// (<c>OAuth2.Tests/Client/OAuth2ClientTests.cs</c>): the substituted
/// <see cref="IRequestFactory"/> hands out a substituted
/// <see cref="IRestClient"/>, so <c>ExecuteAsync</c> never reaches a socket.
/// Responses are selected by the request resource, which the
/// <c>RequestFactoryExtensions</c> set from the client's endpoint definitions.
/// </summary>
public sealed class StubOAuth2Transport
{
    private readonly List<(Func<IRestRequest, bool> Match, IRestResponse Response)> _rules = new();

    /// <summary>Every request the code under test issued, in order.</summary>
    public List<IRestRequest> Requests { get; } = new();

    /// <summary>The substituted rest client. Never a real <see cref="RestClient"/>.</summary>
    public IRestClient Client { get; }

    /// <summary>The seam handed to <see cref="OAuth2ClientFactory"/>.</summary>
    public IRequestFactory Factory { get; }

    public StubOAuth2Transport()
    {
        Client = Substitute.For<IRestClient>();
        Client
            .ExecuteAsync(Arg.Any<IRestRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => _respond((IRestRequest)callInfo[0]));

        Factory = Substitute.For<IRequestFactory>();
        Factory.CreateClient().Returns(_ => Client);
        // A fresh request per call, otherwise parameters of the token call would
        // leak into the subsequent user-info call.
        Factory.CreateRequest().Returns(_ => new RestRequest());
    }

    /// <summary>
    /// Registers a canned response for every request whose resource contains
    /// <paramref name="resourceFragment"/>.
    /// </summary>
    public StubOAuth2Transport RespondTo(
        string resourceFragment,
        HttpStatusCode statusCode,
        string content)
    {
        _rules.Add((
            request => request.Resource != null
                       && request.Resource.Contains(resourceFragment, StringComparison.OrdinalIgnoreCase),
            new RestResponse
            {
                StatusCode = statusCode,
                Content = content,
                ResponseStatus = ResponseStatus.Completed
            }));
        return this;
    }

    private IRestResponse _respond(IRestRequest request)
    {
        Requests.Add(request);

        foreach (var (match, response) in _rules)
        {
            if (match(request))
            {
                return response;
            }
        }

        throw new InvalidOperationException(
            $"No stubbed response for resource '{request.Resource}'. "
            + "The test would have had to go to the network.");
    }
}
