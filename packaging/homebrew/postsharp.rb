cask "postsharp" do
  version "1.11.11"
  sha256 "b526e4cdd81c97a97f68e04310ce6759d52dfe096be7262eb9edc090245c4530"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end