cask "postsharp" do
  version "1.11.17"
  sha256 "ef671e81430868ec9702667d27c424764c78b0c70bc8297c2b0c8517ae538225"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end