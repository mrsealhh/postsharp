cask "postsharp" do
  version "1.11.1"
  sha256 "5650d28897ebb9f833bd1b12d4dfa358b5fc563965daa4a1d3546709d23dff0b"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end