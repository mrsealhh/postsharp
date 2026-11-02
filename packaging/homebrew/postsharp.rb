cask "postsharp" do
  version "1.11.10"
  sha256 "7bd6a50eaa1eb31843d5b9a83d06f384691b0304e588425cb801ab1ceb8e4e06"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end