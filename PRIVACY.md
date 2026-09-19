# Privacy Policy

Solution Bundler does not collect telemetry, analytics, personal information, or usage data, and it does not send data to the project maintainer or any third-party service.

The application reads only the local folders and files explicitly selected by the user. It connects to SQL Server or Redis only when the user explicitly provides or selects a connection and starts the related operation. Database content is written only to output files selected by the user.

Connection strings and application preferences may be read from local `appsettings*.json` files selected through the solution folder. The last selected folder, Favorites, and hidden-folder preferences are stored locally under the current user's application-data directory. These settings are not transmitted anywhere.

In short: **this program will not transfer any information to other networked systems unless specifically requested by the user operating it.**

Questions or security reports can be submitted through the repository's GitHub Issues page.
