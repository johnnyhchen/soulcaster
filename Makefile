.PHONY: build test

build:
	dotnet build Soulcaster.sln --nologo

test:
	dotnet test tests/Soulcaster.Tests/Soulcaster.Tests.csproj --nologo
