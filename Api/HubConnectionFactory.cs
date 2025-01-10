using Microsoft.AspNetCore.SignalR.Client;

namespace Api;

public class HubConnectionFactory
{
    public HubConnection CreateConnection(string hubUrl)
    {
        var connection = new HubConnectionBuilder().WithUrl(hubUrl).Build(); // Set up your event handlers 
        connection.On<string>("ReceiveMessage", (message) =>
        {
            Console.WriteLine($"Received message from {hubUrl}: {message}");
        });
        return connection;
    }
}