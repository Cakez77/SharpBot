using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

internal class SharpBot
{
  private static HttpClient Client = new HttpClient();
  private static ClientWebSocket WebSocket = new ClientWebSocket();

  private static void Main(string[] args)
  {
    /// IMPORTANT The EventSub WebSocket server supports only outgoing messages. 
    /// If you send a message to the server, except for Pong messages, the server closes the connection.

    WebSocketReceiveResult receiveResult;
    byte[] buffer = new byte[8192];
    string sessionID;
    string message;
    EventMessage messageDeserialized;

    GetNewAccessToken();
    GetChannelID();

    // Create WebSocket connection
    // We need to set client ID and auth before connecting
    WebSocket.Options.SetRequestHeader("Client-Id", Config.BotID);
    WebSocket.Options.SetRequestHeader("Authorization", $"Bearer {Config.BotAccessToken}");
    WebSocket.ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"), CancellationToken.None).Wait();
    // Check if it worked
    receiveResult = WebSocket.ReceiveAsync(buffer, CancellationToken.None).Result;
    if (receiveResult.Count <= 0)
    {
      throw new Exception("Couldn't connect to wss://eventsub.wss.twitch.tv/ws.");
    }
    else
    {
      message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
      Console.WriteLine("Got welcome message.");
      // Parse welcome message
      WelcomeMessage welcomeMessage = WelcomeMessage.Deserialize(message);
      if (welcomeMessage?.Payload?.Session?.ID is null) throw new Exception("Couldn't read session ID.");
      else sessionID = welcomeMessage.Payload.Session.ID;

      // Subscribe to every event you want to
      // We have <10 sec to subscribe to an event, also another connection has to be used because we can't send messages to websocket server
      Console.WriteLine("Subscribing to channel follow event.");
      using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("POST"), "https://api.twitch.tv/helix/eventsub/subscriptions"))
      {
        request.Headers.Add("Client-Id", Config.BotID);
        request.Headers.Add("Authorization", $"Bearer {Config.BotAccessToken}");
        request.Content = new StringContent(new SubscriptionMessage("channel.follow", Config.ChannelID, sessionID).ToJsonString());
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        ResponseMessage response = ResponseMessage.Deserialize(Client.SendAsync(request).Result.Content.ReadAsStringAsync().Result);
        if (response.Error is not null) Console.WriteLine(string.Concat("Error: ", response.Message));
        else Console.WriteLine(string.Concat("Response: ", response.Data?[0].Type, " ", response.Data?[0].Status, "."));
      }
    }

    while (true)
    {
      if (WebSocket.State == WebSocketState.Open)
      {
        receiveResult = WebSocket.ReceiveAsync(buffer, CancellationToken.None).Result;
        if (receiveResult.Count <= 0) { Console.WriteLine($"Received {receiveResult.Count} bytes."); }
        else
        {
          message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
          messageDeserialized = EventMessage.Deserialize(message);
          if (messageDeserialized?.Metadata?.MessageType?.Equals("session_keepalive") == true)
          {
            // Keep alive message, if it wasn't received in "keepalive_timeout_seconds" time from welcome message the connection should be restarted
            Console.WriteLine("Got keepalive message.");
          }
          else if (messageDeserialized?.Metadata?.MessageType?.Equals("notification") == true)
          {
            // Received notification event
            if (messageDeserialized?.Metadata?.SubscriptionType?.Equals("channel.follow") == true)
            {
              // Received channel follow event
              Console.WriteLine($"New follow from {messageDeserialized?.Payload?.Event?.UserName}.");
            }
          }
          else
          {
            // Some other message, print it
            Console.WriteLine(message);
          }
        }
      }
      else
      {
        // Do something? Reconnect? For now do nothing.
        Console.WriteLine("Socket not opened. Closing the program.");
        break;
      }

      Thread.Sleep(100);
    }
  }

  static void GetChannelID()
  {
    using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("GET"), "https://api.twitch.tv/helix/users?login=" + Config.ChannelName))
    {
      request.Headers.Add("Client-Id", Config.BotID);
      request.Headers.Add("Authorization", $"Bearer {Config.BotAccessToken}");

      ChannelIDResponse response = ChannelIDResponse.Deserialize(Client.SendAsync(request).Result.Content.ReadAsStringAsync().Result);
      // Read information from received data
      if (response?.Data?[0].ID is null)
      {
        throw new Exception(string.Concat(">> Couldn't acquire broadcaster ID. Probably defined channel name doesn't exist.", Environment.NewLine, ">> Event bot initialization failed."));
      }
      else
      {
        Config.ChannelID = response.Data[0].ID;
      }
    }
  }

  static void GetNewAccessToken()
  {
    string uri = string.Concat(
      "https://id.twitch.tv/oauth2/authorize?",
      "client_id=", Config.BotID,
      "&redirect_uri=http://localhost:3000",
      "&response_type=code",
      // When asking for permissions the scope of permissions has to be determined 
      // if tried to follow to event without getting permissions for it, the follow returns an error
      "&scope=",
        string.Concat(
          "bits:read", // View Bits information for a channel
          "+channel:read:redemptions", // View Channel Points custom rewards and their redemptions on a channel.
          "+moderator:read:followers" // Read followers, needs to be a moderator...
        ).Replace(":", "%3A") // Change to url encoded
      );

    // Open the link for the user to complete authorization
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo() { FileName = uri, UseShellExecute = true });

    // Local server is needed to get response to user authorizing the app (to grab the access token)
    HttpListener localServer = new HttpListener();
    localServer.Prefixes.Add("http://localhost:3000/"); // Where local server should listen for connections, maybe it should be in Config.ini? Hmm
    localServer.Start();
    HttpListenerContext context = localServer.GetContext(); // Await connection

    // For now lets just redirect to twitch to hide received code in browser url
    using (HttpListenerResponse resp = context.Response)
    {
      resp.Headers.Set("Content-Type", "text/plain");
      resp.Redirect(@"https://www.twitch.tv");
    }

    // Close local server, it's no longer needed
    localServer.Close();

    string requestUrl = context.Request.Url != null ? context.Request.Url.Query : string.Empty;
    // Parse received request url
    if (requestUrl.StartsWith("?code="))
    {
      // Next step - request user token with received authorization code
      string code = requestUrl.Substring(6, requestUrl.IndexOf('&', 6) - 6);
      using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("POST"), "https://id.twitch.tv/oauth2/token"))
      {
        request.Content = new StringContent(string.Concat(
            "client_id=", Config.BotID,
            "&client_secret=", Config.BotSecret,
            "&code=", code,
            "&grant_type=authorization_code",
            "&redirect_uri=http://localhost:3000")
          );
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");

        AccessTokenResponse response = AccessTokenResponse.Deserialize(Client.SendAsync(request).Result.Content.ReadAsStringAsync().Result);
        if (response is null || response.Token is null || response.RefreshToken is null) throw new Exception("");
        Console.WriteLine(response.ToString());
        // Read information from received data
        Config.BotAccessToken = response.Token;
        Config.BotRefreshToken = response.RefreshToken;
      }
    }
    else
    {
      // Something went wrong
      throw new NotImplementedException("Implement something here :)");
    }
  }
}
