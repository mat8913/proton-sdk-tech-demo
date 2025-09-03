{ fetchFromGitHub }:

{
  dotnet-crypto = fetchFromGitHub {
    owner = "ProtonDriveApps";
    repo = "dotnet-crypto";
    rev = "5aac829ce0ab4b21f7ad61b4c5b348b168e94d9b";
    hash = "sha256-+YrM4ByfOZJr8rl+VV/j6I6+uvUVRUDTCH8xKBxkFYU=";
  };
}
