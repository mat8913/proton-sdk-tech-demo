# proton-sdk-tech-demo

## Introduction

This repository is a fork of the official Proton SDK tech demo which can be
found [here](https://github.com/ProtonDriveApps/sdk-tech-demo).

This fork contains the necessary changes to implement the features I want in my
own applications. I am trying to keep the changeset as small as possible. You
can view the full set of changes
[here](https://github.com/ProtonDriveApps/sdk-tech-demo/compare/0.2.10...mat8913:proton-sdk-tech-demo:0.2.10-mat8913?expand=1).

Just in case you haven't read it yet, I will quote the warning from the official
repo's README file:

> There will be no public support nor documentation for this code, it is only
> published for demonstration purposes and its use by 3rd party applications is
> strongly discouraged.

## Build

Firstly, make sure you have a local nuget repo set up. You can do that with the
following:

```sh
mkdir ~/local-nuget-repository
dotnet nuget add source ~/local-nuget-repository -n local-nuget
```

Then clone Proton's dotnet-crypto library from
[here](https://github.com/ProtonDriveApps/dotnet-crypto) and build and pack it
with:

```sh
./build/build-go.sh linux/amd64 linux/arm64
dotnet pack -c Release -p:Version=0.10.4 src/dotnet/Proton.Cryptography.csproj --output ~/local-nuget-repository
```

Finally, you can build and pack this repo with:

```sh
dotnet pack -c Release -p:Version=0.2.10-mat8913 src/Proton.Sdk/Proton.Sdk.csproj --output ~/local-nuget-repository
dotnet pack -c Release -p:Version=0.2.10-mat8913 src/Proton.Sdk.Instrumentation/Proton.Sdk.Instrumentation.csproj --output ~/local-nuget-repository
dotnet pack -c Release -p:Version=0.2.10-mat8913 src/Proton.Sdk.Drive/Proton.Sdk.Drive.csproj --output ~/local-nuget-repository
```
