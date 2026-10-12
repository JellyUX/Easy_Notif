## [1.2.0](https://github.com/JellyUX/Easy_Notif/compare/v1.1.1...v1.2.0) (2026-10-12)

### Features

* add a generic template for manual emails ([a05e42f](https://github.com/JellyUX/Easy_Notif/commit/a05e42fec1ebc834b264f1ff5362f498cf30e1bf))
* add a resume section to the weekly recap ([07d6986](https://github.com/JellyUX/Easy_Notif/commit/07d698664a6b2090aae13c8c51c7a32ac47d56c5))
* email a daily incident digest to the admin ([4a2280b](https://github.com/JellyUX/Easy_Notif/commit/4a2280be1ec582ad8b71a7ed41df0b1665667964))
* expose drain health in the admin status ([693544b](https://github.com/JellyUX/Easy_Notif/commit/693544bac81de2bb6563a124cb459bb07a8971de))
* invite users without a contact email ([bb62787](https://github.com/JellyUX/Easy_Notif/commit/bb627873bfbb8cebab5864305b4551323cb0f6d5))
* let users pick newsletter libraries ([4fa708f](https://github.com/JellyUX/Easy_Notif/commit/4fa708f9a0b4afd6c62ce35d2142be1bf26ca362))
* move the transport status to its own admin tab ([98b2983](https://github.com/JellyUX/Easy_Notif/commit/98b298324a2810fb2d245e48d57890aadbbe0f59))
* preview the weekly recap with your own data ([fb3de98](https://github.com/JellyUX/Easy_Notif/commit/fb3de9872bc6c6a03e73d02a451aff9fd45c5cd2))
* schedule manual announcements ([2e17a54](https://github.com/JellyUX/Easy_Notif/commit/2e17a54684a94f32e7969e42933dcc97515c8e75))
* seed the yearly total from existing play data ([8aeed34](https://github.com/JellyUX/Easy_Notif/commit/8aeed344843504e397af1b771b4d881199b68223))
* show every category in the admin preferences ([ceecab0](https://github.com/JellyUX/Easy_Notif/commit/ceecab039e8b50c61381cee6bd0225c6248e683f))

### Bug Fixes

* describe the announcement opt-out on the manual email tab ([fbb974d](https://github.com/JellyUX/Easy_Notif/commit/fbb974d97c0b15d9b1961cb6891ae42e251dad72))
* escape apostrophes in web helpers ([0291261](https://github.com/JellyUX/Easy_Notif/commit/0291261ef53c62b1720c382c9def863c81f77655))
* follow the Jellyfin language on the admin page ([ff66d2d](https://github.com/JellyUX/Easy_Notif/commit/ff66d2d7495090d5f074eb3d0cf00f168fd0fe13))
* keep omitted sender settings unchanged ([6feaa9e](https://github.com/JellyUX/Easy_Notif/commit/6feaa9ef24956fda9ae4f15b8cd5fa05cff41e49))
* keep the invitation actions together on phones ([abfcd6f](https://github.com/JellyUX/Easy_Notif/commit/abfcd6f3ff61d498bc781eb42bad896e8cad5e82))
* show why scheduling is off when attachments are added ([fe11118](https://github.com/JellyUX/Easy_Notif/commit/fe111188e9a9448ac96fee2c7f2f128e8537d60c))
* version the admin page assets ([85a4b45](https://github.com/JellyUX/Easy_Notif/commit/85a4b451e8f9ef7021ee28f056d23516afeb75a5))

## [1.1.1](https://github.com/JellyUX/Easy_Notif/compare/v1.1.0...v1.1.1) (2026-10-09)

### Bug Fixes

* bound and deduplicate Resend webhooks ([eb1cf92](https://github.com/JellyUX/Easy_Notif/commit/eb1cf92455d320b4cfebe0c891ef5f381bcb9912))
* confine and validate inline poster files ([dabf7c1](https://github.com/JellyUX/Easy_Notif/commit/dabf7c1ef26e5be11240db4e82f3e4716c1319f2))
* downscale inline posters through the Jellyfin image processor ([e69df20](https://github.com/JellyUX/Easy_Notif/commit/e69df20612abea02c3461446c1008cd28cd74c3d))
* keep masked addresses stable when masked twice ([f7dca9c](https://github.com/JellyUX/Easy_Notif/commit/f7dca9ccf21fd5ac6a79bfa8f791d12cd8551301))
* mask personal data in every log line ([c583df3](https://github.com/JellyUX/Easy_Notif/commit/c583df3b2fc872772b22431ff2e736457ef5e04b))
* stop mailing addresses that complained or hard-bounced ([220be0f](https://github.com/JellyUX/Easy_Notif/commit/220be0f262f954c4c7e574ba48c3291a52345b21))
* validate template ids against path traversal ([6ebad35](https://github.com/JellyUX/Easy_Notif/commit/6ebad352228cb6bcd6ebe1c1cf2b6a701599bdbb))

## [1.1.0](https://github.com/JellyUX/Easy_Notif/compare/v1.0.2...v1.1.0) (2026-10-08)

### Features

* add a follow bell on series pages ([555b354](https://github.com/JellyUX/Easy_Notif/commit/555b354608f888861f0041b50df5bec44f6dcd80))
* add a per-user access filter ([6e732ff](https://github.com/JellyUX/Easy_Notif/commit/6e732ffa04fde1b4672820ce9f8537b53e3198b3))
* add a schema migration hook to file stores ([2e91083](https://github.com/JellyUX/Easy_Notif/commit/2e91083c0ffd5e0a8097fc0b145b59f580e8d198))
* add followed-series endpoints ([0ebaed2](https://github.com/JellyUX/Easy_Notif/commit/0ebaed26fb96c711c07a7780447d6e4213a593d4))
* add per-series unsubscribe tokens ([1ba6f63](https://github.com/JellyUX/Easy_Notif/commit/1ba6f638b6b5b61556f2f255ffd76969673afceb))
* add the new-episodes email template ([f2a1a92](https://github.com/JellyUX/Easy_Notif/commit/f2a1a92bab1e6aa690588524164e963e0e1eaa98))
* add the series notification store ([57ceb28](https://github.com/JellyUX/Easy_Notif/commit/57ceb2818d9013996c4910b8cea6b1ca8e6f17fa))
* allow disabling the web UI injection ([888d1cc](https://github.com/JellyUX/Easy_Notif/commit/888d1ccf47c7fe45b691ee7405c2ad146d645be2))
* budget the quota for series notifications ([3660678](https://github.com/JellyUX/Easy_Notif/commit/3660678f0fa57b06af27e377f92d3735e314f7e7))
* cap the newsletter size ([7ca9c74](https://github.com/JellyUX/Easy_Notif/commit/7ca9c74b7a8244f6c211ca5ded7152fd2cebabca))
* expire unsubscribe tokens and rate-limit the endpoint ([f8890f2](https://github.com/JellyUX/Easy_Notif/commit/f8890f2450f12e315d8d750edef6edb818d8fec4))
* flush followed-series episodes on a debounce window ([d305e98](https://github.com/JellyUX/Easy_Notif/commit/d305e980554c7d56857c935e7a08fec41da96bfd))
* list followed series in the preferences panel ([555d783](https://github.com/JellyUX/Easy_Notif/commit/555d783f08231aa21ac502ccb45b05882ef17f5a))
* make the sending quota configurable ([bb4399c](https://github.com/JellyUX/Easy_Notif/commit/bb4399cc9a0e3f0cb192d4f91df5e5ddc621e6ef))
* queue new episodes of followed series ([20a88c8](https://github.com/JellyUX/Easy_Notif/commit/20a88c827fbf820da3bc59e5c7607f989f088ef3))
* send manual emails in the background ([4b9ba4c](https://github.com/JellyUX/Easy_Notif/commit/4b9ba4c07e6a0658f3e08153c67253a5223281c3))

### Bug Fixes

* add security headers to the unsubscribe page ([f7134da](https://github.com/JellyUX/Easy_Notif/commit/f7134da544af1a74864ea634f1b89d44c94c43f4))
* honour unsubscribe on manual announcements ([4b34dca](https://github.com/JellyUX/Easy_Notif/commit/4b34dcaf6952d03147aedba744c6b14283f7f60d))
* isolate campaign failures during dispatch ([3190e48](https://github.com/JellyUX/Easy_Notif/commit/3190e48ce63063f733d7fea18252fdc88c753e4c))
* let the action bar wrap so the series bell stays on screen ([25c0cb2](https://github.com/JellyUX/Easy_Notif/commit/25c0cb260cd27eb73c0204a44d5abec40dd2c517))
* make every send idempotent across restarts ([eef8afc](https://github.com/JellyUX/Easy_Notif/commit/eef8afcb576dc7161c993d3bad7e01dc4125b955))
* purge series subscriptions with the user or series ([c2edf8d](https://github.com/JellyUX/Easy_Notif/commit/c2edf8daaa69690fc220afb2ad4800588d00bfc7))
* reject unsubscribe tokens of deleted users ([d63d1a6](https://github.com/JellyUX/Easy_Notif/commit/d63d1a62c173b838dd7090e842bc5d5aa43fdbce))
* retry failed recipients of a campaign run ([80fbfdd](https://github.com/JellyUX/Easy_Notif/commit/80fbfdd224c2a41d67a6074530823ec98e99aba6))
* scope the newsletter to each recipient's library access ([45bd0bc](https://github.com/JellyUX/Easy_Notif/commit/45bd0bc5f0fc600f7d3a9e76f938024846d6653c))
* skip announced and pre-subscription episodes in the series catch-up ([552fb09](https://github.com/JellyUX/Easy_Notif/commit/552fb09406cd6f1397b55f6f800a67b4207ce05a))
* skip disabled Jellyfin accounts ([f056550](https://github.com/JellyUX/Easy_Notif/commit/f056550957aaba0376db70851223adc41a3455a1))
* stop re-announcing media already sent ([ae52e89](https://github.com/JellyUX/Easy_Notif/commit/ae52e89ea9aa55d7757cdcc977b44b0268f974e2))
* validate campaign intervals and isolate startup steps ([57e1a36](https://github.com/JellyUX/Easy_Notif/commit/57e1a3688c2d7bb2db1037af251df7709b21406c))

## [1.0.2](https://github.com/JellyUX/Easy_Notif/compare/v1.0.1...v1.0.2) (2026-09-09)

### Bug Fixes

* validate the public server URL before storing it ([12a0097](https://github.com/JellyUX/Easy_Notif/commit/12a00970415ec8e566e18dcd22dd855e99bfc947))

## [1.0.1](https://github.com/JellyUX/Easy_Notif/compare/v1.0.0...v1.0.1) (2026-09-09)

### Bug Fixes

* cap template nesting depth so a preview cannot crash the server ([8a2ade7](https://github.com/JellyUX/Easy_Notif/commit/8a2ade7329be722f7bf3fecc8fc777f461922a00))
* keep only the most recent corrupt-file backup ([de3a2d5](https://github.com/JellyUX/Easy_Notif/commit/de3a2d54025e4cfe0d1c963dd6a840ae0cc1c82c))
* keep the playback and added-item drains alive after a write error ([4b0fa72](https://github.com/JellyUX/Easy_Notif/commit/4b0fa72b1d709fbaf9000ce347bdc9b5b1e93231))
* meter and rate-limit the me/test send path ([ec2ab5d](https://github.com/JellyUX/Easy_Notif/commit/ec2ab5dbbec4744e062a989a6d28a01924810f1d))
* purge a user's data when the Jellyfin account is deleted ([43a00ad](https://github.com/JellyUX/Easy_Notif/commit/43a00ad10418ad6dcf6caf2cc973be6720916d59))
* store transport secrets outside the serialized plugin configuration ([680c864](https://github.com/JellyUX/Easy_Notif/commit/680c86429203946eff27e255ce17cd01a3dc809c))

## [1.0.0](https://github.com/JellyUX/Easy_Notif/compare/v0.9.0...v1.0.0) (2026-09-08)

### ⚠ BREAKING CHANGES

* from 1.0.0 the /EasyNotif HTTP API (routes, verbs, status
codes) and the schema:1 on-disk file formats (preferences.json, campaigns.json,
quota.json, added-items.json, playback-history.json) are covered by SemVer. The
unused PluginConfiguration.Enabled and PlaybackBackfilled fields are removed;
existing config XML keeps loading because XmlSerializer ignores unknown elements.

### Features

* stabilise the public API and file formats for 1.0.0 ([7b965cf](https://github.com/JellyUX/Easy_Notif/commit/7b965cfc61a5bebe333920bc24b1d2ffec477f8e))

### Bug Fixes

* render the startup warning banner in the warning colour ([9d60e71](https://github.com/JellyUX/Easy_Notif/commit/9d60e7145ac305dd28cef823107327b505ea8533))
* return 503 when a storage error hits the unsubscribe endpoint ([3e3c946](https://github.com/JellyUX/Easy_Notif/commit/3e3c946afe68fe7532e84d405b77af542108f962))
* write the dev meta.json without a BOM so Jellyfin can parse it ([cd93d5c](https://github.com/JellyUX/Easy_Notif/commit/cd93d5cd4399d8dfb591b7e9be3bf79ba49af87a))

## [0.9.0](https://github.com/JellyUX/Easy_Notif/compare/v0.8.0...v0.9.0) (2026-09-07)

### Features

* add one-click unsubscribe with a signed token ([94e024a](https://github.com/JellyUX/Easy_Notif/commit/94e024a471f46b55322f16f36422cad105c423a4))
* attach posters inline when no public server url is set ([5d537a6](https://github.com/JellyUX/Easy_Notif/commit/5d537a6ca6b3f80b9a6ac494afdb48b0c0695256))
* expose campaigns and file-transformation state in the status endpoint ([50d79ff](https://github.com/JellyUX/Easy_Notif/commit/50d79ffa0f894a9cdfd01e0d2abbc65507ca84e5))
* let each campaign choose a custom email template ([6cbe9d6](https://github.com/JellyUX/Easy_Notif/commit/6cbe9d6416618c88a27907451c160eb12bf6379a))

### Bug Fixes

* add french accents to email and unsubscribe-page text ([ce5cfd9](https://github.com/JellyUX/Easy_Notif/commit/ce5cfd959a8c296b8e83b57a812917393181245a))
* attach the original poster capped at 500 KB instead of resizing ([868a035](https://github.com/JellyUX/Easy_Notif/commit/868a03568ebae3b404753c21e50289a772343568))
* make the unsubscribe token a single url-safe segment ([8430a55](https://github.com/JellyUX/Easy_Notif/commit/8430a550f5b15d6ab22245e3753279a5316c8e9c))
* show the specific template error in red and call it a template in french ([5718ba0](https://github.com/JellyUX/Easy_Notif/commit/5718ba03bcfbeae87e3e09e2c5a5e40fbfd620e2))

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
