cask "postsharp" do
  version "1.11.7"
  sha256 "48feb3738ec01e39041133e9479810c97ba402290fc7987df0b28b722e799d5f"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end