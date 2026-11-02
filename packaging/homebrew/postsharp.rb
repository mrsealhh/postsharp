cask "postsharp" do
  version "1.11.15"
  sha256 "61a05018dbabd59089120febc93bcb99b81b5dc05094222a97f81555c1ae8be3"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end