# Rescore: ollama-qwen3-8b

Stored runs re-scored with the tolerant answer parser; no model calls. Change tasks keep their stored result.

| Mode | Runs | Old success | New success | New via lenient parse |
| --- | ---: | ---: | ---: | ---: |
| plain | 5 | 0 | 0 | 0 |
| codeatlas | 6 | 0 | 0 | 0 |

| Task | Mode | Run | Old | New | Answer from | Parse | Failed checks |
| --- | --- | ---: | --- | --- | --- | --- | --- |
| find-password-verification | plain | 1 | False | False | final-text | json-object | files: {"missing": ["jellyfin.server.implementations/users/defaultauthenticationprovider.cs"], "forbidden": []}; symbols: {"missingGroups": [["DefaultAuthenticationProvider.Authenticate"]]} |
| find-password-verification | codeatlas | 1 | False | False | final-text | json-object | files: {"missing": ["jellyfin.server.implementations/users/defaultauthenticationprovider.cs"], "forbidden": []}; symbols: {"missingGroups": [["DefaultAuthenticationProvider.Authenticate"]]} |
| find-episode-number-parsing | codeatlas | 1 | False | False | - | - | files: {"detail": "answer.files is not a list"}; symbols: {"detail": "answer.symbols is not a list"} |
| find-episode-number-parsing | plain | 1 | False | False | final-text | json-object | files: {"missing": ["emby.naming/tv/episodepathparser.cs"], "forbidden": []} |
| impact-metadata-saver-implementations | plain | 1 | False | False | final-text | json-object | symbols: {"missing": ["AlbumNfoSaver", "ArtistNfoSaver", "BoxSetXmlSaver", "EpisodeNfoSaver", "MovieNfoSaver", "PlaylistXmlSaver", "SeasonNfoSaver", "SeriesNfoSaver"], "forbidden": []} |
| impact-metadata-saver-implementations | codeatlas | 1 | False | False | - | - | symbols: {"detail": "answer.symbols is not a list"} |
| impact-static-media-sources | codeatlas | 1 | False | False | - | - | files: {"detail": "answer.files is not a list"} |
| impact-static-media-sources | plain | 1 | False | False | final-text | json-object | files: {"missing": ["emby.server.implementations/dto/dtoservice.cs", "jellyfin.api/controllers/subtitlecontroller.cs"], "forbidden": []} |
