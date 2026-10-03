using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;

namespace SIRIAUTOPOST.Api.IntegrationTests;

public class PostsEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_then_get_list_update_and_delete()
    {
        var create = await _client.PostAsJsonAsync("/api/posts",
            new { content = "สวัสดี", groupUrl = "facebook.com/groups/42", scheduledAt = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<PostDto>();
        Assert.Equal("https://www.facebook.com/groups/42/", created!.GroupUrl);

        var fetched = await _client.GetFromJsonAsync<PostDto>(create.Headers.Location);
        Assert.Equal(created.Id, fetched!.Id);

        var list = await _client.GetFromJsonAsync<List<PostDto>>("/api/posts");
        Assert.Contains(list!, p => p.Id == created.Id);

        var update = await _client.PutAsJsonAsync($"/api/posts/{created.Id}",
            new { content = "แก้แล้ว", scheduledAt = DateTimeOffset.UtcNow.AddDays(1) });
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<PostDto>();
        Assert.Equal("แก้แล้ว", updated!.Content);
        Assert.Equal("Scheduled", updated.Status);

        var delete = await _client.DeleteAsync($"/api/posts/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/posts/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Missing_fields_return_validation_problem()
    {
        var res = await _client.PostAsJsonAsync("/api/posts", new { content = "", groupUrl = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("content", problem!.Errors.Keys);
        Assert.Contains("groupUrl", problem.Errors.Keys);
    }

    [Fact]
    public async Task Non_group_link_breaks_a_domain_rule()
    {
        var res = await _client.PostAsJsonAsync("/api/posts", new { content = "hi", groupUrl = "https://example.com" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }
}
