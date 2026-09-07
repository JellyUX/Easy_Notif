## [0.8.0](https://github.com/JellyUX/Easy_Notif/compare/v0.7.0...v0.8.0) (2026-09-07)

### Features

* add the personalised weekly watch recap ([bc33662](https://github.com/JellyUX/Easy_Notif/commit/bc33662ed0dc604c27e9bead84443805f8703ebe))
* add the playback history store ([270ab7d](https://github.com/JellyUX/Easy_Notif/commit/270ab7d65a84c901e5ed30c5d12068863d2e5676))
* record significant playback stops ([9527fa2](https://github.com/JellyUX/Easy_Notif/commit/9527fa267363092364ceb6b14bb5f7b9aba3c0fb))

### Bug Fixes

* count a repeated playback stop for the same view once ([c2e7184](https://github.com/JellyUX/Easy_Notif/commit/c2e7184e4fda007796ba376889f6aa44df4d1513))
* send every manual campaign run instead of deduplicating it ([883b64a](https://github.com/JellyUX/Easy_Notif/commit/883b64a77b769d9c8408dc999d2f81019f4babc2))

## [0.7.0](https://github.com/JellyUX/Easy_Notif/compare/v0.6.0...v0.7.0) (2026-09-06)

### Features

* add the new-media digest service ([a262811](https://github.com/JellyUX/Easy_Notif/commit/a262811f9f8ffbbe40b87976e413dde65f390eb4))
* add the templating engine and the newsletter template ([d9de1b9](https://github.com/JellyUX/Easy_Notif/commit/d9de1b93b6c1e1bf9ca23f56c6aea74049db5fac))
* track new media by server add-time via the ItemAdded event ([00fa82e](https://github.com/JellyUX/Easy_Notif/commit/00fa82e4add13736cc9db56d080f2ede8f897b5c))
* wire the newsletter campaign and its preview ([2a55e4c](https://github.com/JellyUX/Easy_Notif/commit/2a55e4c7c361cde7837517265aa4a1b31cda6706))

### Bug Fixes

* key idempotency by the scheduled slot, preview a fresh 7-day window ([3642468](https://github.com/JellyUX/Easy_Notif/commit/3642468ae46cc3e91cb0f944f763e598d04bf5e5))
* treat a 409 idempotency conflict as a deduplicated send ([850a7e9](https://github.com/JellyUX/Easy_Notif/commit/850a7e9e7560ecb7499b4a4ae40c2d25925f9ea7))
* window the newsletter digest by DateCreated in memory ([ac769cf](https://github.com/JellyUX/Easy_Notif/commit/ac769cf8329de8c0f163710b5a455d4c53e9a371))

## [0.6.0](https://github.com/JellyUX/Easy_Notif/compare/v0.5.0...v0.6.0) (2026-09-06)

### Features

* add the campaign store with the two system campaigns ([ec851e8](https://github.com/JellyUX/Easy_Notif/commit/ec851e8a515753587dbfa17569b41f5b6a7ab3e5))
* add the campaigns config tab ([6f7df1b](https://github.com/JellyUX/Easy_Notif/commit/6f7df1b67bf996d82d7f5115e798268ea7bab550))
* add the dispatch service and its scheduled task ([20e793a](https://github.com/JellyUX/Easy_Notif/commit/20e793a6048474a894d230e02a567762dd36f4d6))
* add the recurrence schedule value object ([91eada4](https://github.com/JellyUX/Easy_Notif/commit/91eada4af4cfd5db5c874cbb24f887d2f3f1c7e9))

### Bug Fixes

* make the campaign enabled toggle reliably clickable ([eb5bbac](https://github.com/JellyUX/Easy_Notif/commit/eb5bbac758e4f7c22f44fa044cd6d5e426a31660))
* show campaign run times in the configured time zone ([2034183](https://github.com/JellyUX/Easy_Notif/commit/2034183f548c8d1efa630f1dd89fe63c7dabe91f))

## [0.5.0](https://github.com/JellyUX/Easy_Notif/compare/v0.4.0...v0.5.0) (2026-09-04)

### Features

* add a dedicated rolling log file ([4ba2e13](https://github.com/JellyUX/Easy_Notif/commit/4ba2e1378b719774b2c8c812b608d8321b6b9cd7))
* instrument the plugin events and expose a logs tab ([a5599d4](https://github.com/JellyUX/Easy_Notif/commit/a5599d442eca53c54771396f66c8c6274be0a0bc))

## [0.4.0](https://github.com/JellyUX/Easy_Notif/compare/v0.3.0...v0.4.0) (2026-09-04)

### Features

* add the manual email composer tab ([a375c45](https://github.com/JellyUX/Easy_Notif/commit/a375c451497212b6b09892be572b927dbe2509d9))
* send manual admin emails to all or selected users ([786c8cb](https://github.com/JellyUX/Easy_Notif/commit/786c8cb5432b3819b8a7227bf53685a7c906e79f))

## [0.3.0](https://github.com/JellyUX/Easy_Notif/compare/v0.2.0...v0.3.0) (2026-09-04)

### Features

* add the Resend email sender with rate limiting and idempotency ([af44c7f](https://github.com/JellyUX/Easy_Notif/commit/af44c7fd16022e6f3dc3bc914ed342c7f73e00f2))
* send a real test email from the user panel ([ab6ff1c](https://github.com/JellyUX/Easy_Notif/commit/ab6ff1c56b83c23604f2a1880964ecb3d54cd684))
* show the Resend transport status on the settings tab ([339fd1f](https://github.com/JellyUX/Easy_Notif/commit/339fd1f670adba052719f21c1946c33da8644e52))
* track the monthly send quota and log every send ([f8779b9](https://github.com/JellyUX/Easy_Notif/commit/f8779b93ad2f4c72481c41da447f184bf64bea34))
* verify and record Resend delivery webhooks ([566d06b](https://github.com/JellyUX/Easy_Notif/commit/566d06b1a244fb9156beaf494d5f59caf5958635))

## [0.2.0](https://github.com/JellyUX/Easy_Notif/compare/v0.1.0...v0.2.0) (2026-09-04)

### Features

* inject the user email notification settings panel ([00cd7e6](https://github.com/JellyUX/Easy_Notif/commit/00cd7e67cac6ace2253b7bf2eb9080d8a4c4c1ca))
* register the index.html transformation and generate the unsubscribe secret ([f1c641f](https://github.com/JellyUX/Easy_Notif/commit/f1c641fd55dfae78035a2a1df6c82acdaa300aa2))
* show the startup warning on the config page ([c409f28](https://github.com/JellyUX/Easy_Notif/commit/c409f2833faadbebd550442d7b66d9cc2c0ffc6d))

## [0.1.0](https://github.com/JellyUX/Easy_Notif/compare/v0.0.1...v0.1.0) (2026-09-03)

### Features

* add the admin config page with settings and preferences tabs ([df38d45](https://github.com/JellyUX/Easy_Notif/commit/df38d4591361c0e1492d719196ac47f14635eb63))
* add the preference service with recipient resolution ([5675679](https://github.com/JellyUX/Easy_Notif/commit/567567980f8ded91c3dfc675bedf322b8b3bfe71))
* add the preferences and settings HTTP API ([ce7d8bc](https://github.com/JellyUX/Easy_Notif/commit/ce7d8bc7f38019726389b3e5cd2e6052d6afcf29))
* add the user preferences store ([726655c](https://github.com/JellyUX/Easy_Notif/commit/726655c7eba04c21286c8363d4961eaf10bcd525))
