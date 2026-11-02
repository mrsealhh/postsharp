cask "postsharp" do
  version "1.11.19"
  sha256 "e7c286c11d0f6cb017c35a385987877bc7059e719a2077aa1405b12865aa8d31"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end