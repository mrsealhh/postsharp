cask "postsharp" do
  version "1.11.0"
  sha256 "d8542b989a7cbc2ff129e8172176af211cbc8ca244a54227b978892d1ce369b1"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end