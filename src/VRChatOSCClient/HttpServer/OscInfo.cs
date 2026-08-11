namespace VRChatOSCClient.HttpServer;

internal static class OscInfo {
  
    private const string Info = """
        {
          "DESCRIPTION": "root node",
          "FULL_PATH": "/",
          "ACCESS": 0,
          "CONTENTS": {
            "avatar": {
              "FULL_PATH": "/avatar",
              "ACCESS": 0,
              "CONTENTS": {
                "change": {
                  "DESCRIPTION": "",
                  "FULL_PATH": "/avatar/change",
                  "ACCESS": 2,
                  "TYPE": "s"
                }
              }
            }
          }
        }
        """;

    public static string ToJson() => Info;
}