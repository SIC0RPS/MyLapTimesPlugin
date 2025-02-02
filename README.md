# MyLapTimesPlugin
MyLapTimesPlugin for AssettoServer (v0.0.55)

MyLapTimesPlugin is a lightweight yet powerful plugin for AssettoServer (v0.0.55). It enhances your racing experience by saving and displaying all your lap times. The plugin checks if a lap was clean (tracking the number of "cuts") and provides real-time lap-time feedback directly in the in-game chat. It also integrates with Discord via a webhook to share lap times effortlessly.

Key Features

    Lap Time Tracking:
    Save and display all lap times, including differentiation between clean laps and laps with cuts.

    Real-Time Feedback:
    Broadcast lap times in the in-game chat to keep racers informed.

    Top Lap Times Leaderboard:
    Maintain a configurable leaderboard that tracks the top N lap times per track (configurable via extra_cfg.yml).

    Discord Integration:
    Automatically send lap times to a Discord channel using a webhook URL.

    Configurable Settings:
    Easily customize plugin behavior with settings such as enabling client messages, setting top lap count limits, and updating webhook URLs.

Getting Started

    Enable the Plugin:
    Add the plugin to your extra_cfg.yml configuration file:

EnablePlugins:
  - MyLapTimesPlugin

Configure the Plugin:
Update the plugin’s configuration file with your desired settings:

    Enabled: true
    DiscordWebhookUrl: ""  # Add your webhook URL here
    MaxTopTimes: 5         # Set the number of top lap times to track
    BroadcastMessages: true

    In-Game Chat Functionality:
    Ensure EnableClientMessages is set to true in your configuration to activate in-game chat feedback.

Installation and Setup

    Place the plugin in the appropriate directory for AssettoServer (v0.0.55).
    Adjust the settings in extra_cfg.yml and the plugin's configuration file as needed.
    Start your AssettoServer instance and enjoy enhanced lap-time tracking and integration.

Contributions are welcome! Feel free to submit issues, feature requests, or pull requests to help improve MyLapTimesPlugin.
