using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using UglyToad.PdfPig;
using System.Net.Http;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();

app.MapPost("/extract", async (HttpRequest request) =>
{
    try
    {
        if (!request.HasFormContentType)
            return Results.BadRequest("El contenido debe ser multipart/form-data.");

        var form = await request.ReadFormAsync();
        var file = form.Files["pdf"];
        var apiKey = form["apikey"].ToString();

        Console.WriteLine("Paso 1: Formulario le�do");

        if (file == null || file.Length == 0)
            return Results.BadRequest("No se recibi� un archivo PDF v�lido.");

        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.BadRequest("API Key no v�lida.");

        Console.WriteLine("Paso 2: Archivo y API key OK");

        string text = string.Empty;
        using (var stream = file.OpenReadStream())
        using (var pdf = PdfDocument.Open(stream))
        {
            text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        }

        Console.WriteLine("Paso 3: Texto extra�do del PDF");

        var prompt = $"""
                        Extrae la siguiente informaci�n del PDF:
                        - Nombre del cliente
                        - Fecha de la factura
                        - Importe total
                        - N�mero de factura

                        Devuelve la respuesta en formato JSON.

                        Texto del documento:
                        {text}
                        """;

        Console.WriteLine("Paso 4: Prompt preparado");

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "http://localhost");
        httpClient.DefaultRequestHeaders.Add("X-Title", "PDF Extractor");

        var requestBody = new
        {
            model = "mistralai/mistral-small-3.2-24b-instruct:free",
            messages = new[]
            {
                new { role = "system", content = "Eres un extractor de datos de documentos PDF." },
                new { role = "user", content = prompt }
            }
        };

        var jsonBody = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        Console.WriteLine("Paso 5: Enviando petici�n a OpenAI");

        var response = await httpClient.PostAsync("https://openrouter.ai/api/v1/chat/completions", content);

        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error OpenAI: C�digo {response.StatusCode}");
            Console.WriteLine("Contenido: " + responseContent);
            return Results.StatusCode((int)response.StatusCode);
        }

        Console.WriteLine("Paso 6: Respuesta recibida de OpenAI");

        var jsonResponse = JsonDocument.Parse(responseContent);

        var output = jsonResponse.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        Console.WriteLine("Paso 7: Contenido extra�do de la respuesta");

        // Validar que output sea JSON v�lido antes de devolver
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine("La respuesta de OpenAI no contiene texto v�lido.");
            return Results.Problem("La respuesta de OpenAI no contiene texto v�lido.");
        }

        output = output.Trim();
        if (output.StartsWith("```json"))
            output = output.Substring(7).Trim();
        if (output.EndsWith("```"))
            output = output.Substring(0, output.Length - 3).Trim();

        try
        {
            var jsonOutput = JsonDocument.Parse(output);
            return Results.Json(jsonOutput.RootElement);
        }
        catch (JsonException)
        {
            Console.WriteLine("Respuesta no es JSON v�lido:");
            Console.WriteLine(output);
            return Results.Content(output, "text/plain");
        }

    }
    catch (Exception ex)
    {
        Console.WriteLine("Excepci�n capturada: " + ex.ToString());
        return Results.Problem("Ocurri� un error al procesar la solicitud.");
    }
});

app.Run();
