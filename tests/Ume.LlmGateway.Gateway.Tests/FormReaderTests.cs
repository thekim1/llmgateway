using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Ume.LlmGateway.Gateway.Forms;
using Ume.LlmGateway.Gateway.Requests;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>The audio and image upload rules on top of the shared multipart reader.</summary>
public sealed class FormReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Audio_form_has_its_fields_and_the_one_file()
    {
        var form = await AudioFormReader.ReadAsync(await RequestAsync(("model", "whisper", null), ("stream", "true", null), ("file", "abc", "a.mp3")), 1024, Ct);

        form.Error.ShouldBeNull();
        form.Fields!["model"]!.GetValue<string>().ShouldBe("whisper");
        form.Fields!["stream"]!.GetValue<bool>().ShouldBeTrue();
        using var audio = form.Audio.ShouldNotBeNull();
        (audio.FileName, audio.Data.Length).ShouldBe(("a.mp3", 3));
    }

    [Fact]
    public async Task Audio_refuses_a_second_file_a_missing_file_and_a_file_in_another_field()
    {
        (await AudioFormReader.ReadAsync(await RequestAsync(("file", "a", "a.mp3"), ("file", "b", "b.mp3")), 1024, Ct)).Error.ShouldBe(GatewayError.OneAudioFile);
        (await AudioFormReader.ReadAsync(await RequestAsync(("model", "whisper", null)), 1024, Ct)).Error.ShouldBe(GatewayError.AudioFileMissing);
        (await AudioFormReader.ReadAsync(await RequestAsync(("file", "", "empty.mp3")), 1024, Ct)).Error.ShouldBe(GatewayError.AudioFileMissing);
        (await AudioFormReader.ReadAsync(await RequestAsync(("audio", "a", "a.mp3")), 1024, Ct)).Error.ShouldBe(GatewayError.OneAudioFile);
    }

    [Fact]
    public async Task Too_large_files_and_fields_are_refused()
    {
        (await AudioFormReader.ReadAsync(await RequestAsync(("file", new string('x', 2000), "a.mp3")), 1024, Ct)).Error!.StatusCode.ShouldBe(413);
        (await ImageFormReader.ReadAsync(await RequestAsync(("image", new string('x', 600), "a.png"), ("image", new string('x', 600), "b.png")), 1100, Ct))
            .Error!.Code.ShouldBe(GatewayErrorCodes.RequestTooLarge);
        (await AudioFormReader.ReadAsync(await RequestAsync(("prompt", new string('x', 70_000), null), ("file", "a", "a.mp3")), 1_000_000, Ct))
            .Error.ShouldBe(GatewayError.FieldTooLarge("prompt"));
    }

    [Fact]
    public async Task Image_form_keeps_pictures_and_the_mask_and_ignores_empty_parts()
    {
        var form = await ImageFormReader.ReadAsync(await RequestAsync(("image[]", "a", "a.png"), ("image[]", "", "empty.png"), ("image[]", "b", "b.png"), ("mask", "m", "m.png"), ("prompt", "p", null)), 1024, Ct);

        form.Error.ShouldBeNull();
        using var images = form.Images.ShouldNotBeNull();
        images.Files.Select(f => (f.Field, f.FileName)).ShouldBe([("image[]", "a.png"), ("image[]", "b.png"), ("mask", "m.png")]);
        images.ImageCount.ShouldBe(2);
    }

    [Fact]
    public async Task Image_refuses_a_second_mask_a_mask_alone_too_many_pictures_and_other_fields()
    {
        (await ImageFormReader.ReadAsync(await RequestAsync(("image", "a", "a.png"), ("mask", "m", "m.png"), ("mask", "n", "n.png")), 1024, Ct))
            .Error.ShouldBe(GatewayError.ImageFields(ImageFormReader.MaxImages));
        (await ImageFormReader.ReadAsync(await RequestAsync(("mask", "m", "m.png")), 1024, Ct)).Error.ShouldBe(GatewayError.ImageMissing);
        (await ImageFormReader.ReadAsync(await RequestAsync([.. Enumerable.Range(0, ImageFormReader.MaxImages + 2).Select(i => ("image[]", "x", (string?)$"{i}.png"))]), 100_000, Ct))
            .Error.ShouldBe(GatewayError.ImageFields(ImageFormReader.MaxImages));
        (await ImageFormReader.ReadAsync(await RequestAsync(("file", "a", "a.png")), 1024, Ct)).Error.ShouldBe(GatewayError.ImageFields(ImageFormReader.MaxImages));
    }

    [Fact]
    public async Task A_body_that_is_not_multipart_is_refused_with_415()
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream("{}"u8.ToArray());

        (await AudioFormReader.ReadAsync(http.Request, 1024, Ct)).Error!.StatusCode.ShouldBe(415);
        (await ImageFormReader.ReadAsync(http.Request, 1024, Ct)).Error!.StatusCode.ShouldBe(415);
    }

    private static async Task<HttpRequest> RequestAsync(params (string Name, string Value, string? FileName)[] parts)
    {
        using var content = new MultipartFormDataContent("test-boundary");
        foreach (var (name, value, fileName) in parts)
        {
            if (fileName is null)
            {
                content.Add(new StringContent(value), name);
            }
            else
            {
                var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(value));
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(file, name, fileName);
            }
        }

        var body = new MemoryStream(await content.ReadAsByteArrayAsync(Ct));
        var http = new DefaultHttpContext();
        http.Request.ContentType = content.Headers.ContentType!.ToString();
        http.Request.ContentLength = body.Length;
        http.Request.Body = body;
        return http.Request;
    }
}
