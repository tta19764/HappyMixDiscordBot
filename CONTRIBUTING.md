# Development and releases

Version v0.0.1 introduces the public bot, feature guides and Ukrainian release announcements.

After v0.0.1, create branches named feature/<short-description> for features or
fix/<short-description> for corrections. Open a pull request targeting main and wait
for the test job to pass before merging. CI validates the branch prefix. Keep main
releasable. Avoid direct pushes to main and never move a published version tag.

For each release, add src/HappyMix.Bot/Releases/vMAJOR.MINOR.PATCH.json with en, uk
and ru arrays of detailed messages, each at most 4,000 characters. Explain user-visible
changes, fixes, limitations and required administrator actions. Update the feature
guides and translations when behavior changes. Run tools/check_release.py and tests.
Merge the pull request, then create an annotated version tag on the main commit and
push the tag. Only version tags build and deploy production images.

After deployment health checks succeed, deploy.sh writes the ready-release marker
inside the persistent data volume. The bot publishes the matching bundled notes in
the configured server's bot-releases channel, creating it when needed. It stores
message IDs and checks recent bot messages when recovering an interrupted send.
A crash after delivery can still duplicate a message if it has already disappeared
from the latest 100 channel messages before retry.

Configure ReleaseAnnouncements:GuildId, ChannelId and Language in appsettings.json
or the corresponding environment variables. ChannelId 0 searches for bot-releases.
The initial server is 887398817404694548 and language is uk. The bot needs View
Channel, Send Messages, Embed Links and Read Message History. Creating the channel
also requires Manage Channels. Patch notes use no role or user pings.

The required GitHub branch protection is a pull request before merging and a passing
test status on main. Repository permissions or GitHub plan restrictions may require
an owner to enable this protection in repository settings.
