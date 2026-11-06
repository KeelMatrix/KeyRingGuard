# Applicable noise for this .NET library; paths are Git paths on every OS.
@(
    @{ Pattern = '**/bin/'; Paths = @('bin/a.dll', 'src/Space Project/bin/a.dll') }
    @{ Pattern = '**/obj/'; Paths = @('obj/a.cache', 'src/Project/obj/a.cache') }
    @{ Pattern = '.vs/'; Paths = @('.vs/state', 'src/.vs/state') }
    @{ Pattern = '.idea/'; Paths = @('.idea/state', 'src/.idea/state') }
    @{ Pattern = '*.user'; Paths = @('Example.user', 'src/Example.user') }
    @{ Pattern = '*.suo'; Paths = @('Example.suo', 'src/Example.suo') }
    @{ Pattern = '*.userprefs'; Paths = @('Example.userprefs', 'src/Example.userprefs') }
    @{ Pattern = '.DS_Store'; Paths = @('.DS_Store', 'src/.DS_Store') }
    @{ Pattern = 'Thumbs.db'; Paths = @('Thumbs.db', 'src/Thumbs.db') }
    @{ Pattern = '[Dd]esktop.ini'; Paths = @('Desktop.ini', 'src/desktop.ini') }
    @{ Pattern = '.cache/'; Paths = @('.cache/state', 'src/.cache/state') }
    @{ Pattern = 'artifacts/'; Paths = @('artifacts/packages/a', 'src/artifacts/a') }
    @{ Pattern = 'TestResults/'; Paths = @('TestResults/a.trx', 'src/TestResults/a.trx') }
    @{ Pattern = '*.log'; Paths = @('build.log', 'src/build.log') }
    @{ Pattern = '*.nupkg'; Paths = @('a.nupkg', 'feed/a.nupkg') }
    @{ Pattern = '*.snupkg'; Paths = @('a.snupkg', 'feed/a.snupkg') }
    @{ Pattern = '.env*'; Paths = @('.env', 'src/.env.local') }
    @{ Pattern = 'local.*.json'; Paths = @('local.secrets.json', 'src/local.settings.json') }
    @{ Pattern = 'coverage/'; Paths = @('coverage/report.xml', 'tools/coverage/report.xml') }
)
