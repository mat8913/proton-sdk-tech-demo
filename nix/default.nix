{ callPackage }:

rec {
  dotnet-crypto-go = callPackage ./dotnet-crypto-go { };

  dotnet-crypto-cs = callPackage ./dotnet-crypto-cs { inherit dotnet-crypto-go; };

  unofficial-pdrive-http-bridge = callPackage ./unofficial-pdrive-http-bridge { inherit dotnet-crypto-cs; };
}
