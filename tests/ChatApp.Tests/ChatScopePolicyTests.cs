using ChatApp.API.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ChatApp.Tests
{
    public class ChatScopePolicyTests
    {
        private IConfiguration CreateConfig(string? threshold = null)
        {
            var dict = new Dictionary<string, string?>();
            if (threshold != null)
            {
                dict["AI:OutOfScopeThreshold"] = threshold;
            }
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        [Fact]
        public void Evaluate_HighConfidenceOutOfScope_IsBlocked()
        {
            var policy = new ChatScopePolicy(CreateConfig());
            var intent = new IntentResult("OutOfScope", 0.99f, "Unrelated trivia");

            var result = policy.Evaluate(intent, "Bhanu");

            Assert.True(result.IsBlocked);
            Assert.Contains("ChatApp customer support assistant", result.RefusalMessage);
            Assert.Contains("Bhanu", result.RefusalMessage);
            Assert.Equal("OutOfScope", result.Intent);
            Assert.Equal(0.99f, result.Confidence);
        }

        [Fact]
        public void Evaluate_OutOfScopeAtExactThreshold_IsBlocked()
        {
            var policy = new ChatScopePolicy(CreateConfig()); // Default is 0.80
            var intent = new IntentResult("OutOfScope", 0.80f, "Borderline out of scope");

            var result = policy.Evaluate(intent);

            Assert.True(result.IsBlocked);
            Assert.Contains("ChatApp customer support assistant", result.RefusalMessage);
        }

        [Fact]
        public void Evaluate_OutOfScopeBelowThreshold_AllowedThroughToDefenseInDepth()
        {
            var policy = new ChatScopePolicy(CreateConfig()); // Default is 0.80
            var intent = new IntentResult("OutOfScope", 0.79f, "Ambiguous out of scope");

            var result = policy.Evaluate(intent);

            Assert.False(result.IsBlocked);
            Assert.Null(result.RefusalMessage);
        }

        [Fact]
        public void Evaluate_ValidFAQ_Allowed()
        {
            var policy = new ChatScopePolicy(CreateConfig());
            var intent = new IntentResult("FAQ", 0.95f, "User asked about messaging");

            var result = policy.Evaluate(intent);

            Assert.False(result.IsBlocked);
            Assert.Null(result.RefusalMessage);
            Assert.Equal("FAQ", result.Intent);
        }

        [Fact]
        public void Evaluate_CreateTicket_Allowed()
        {
            var policy = new ChatScopePolicy(CreateConfig());
            var intent = new IntentResult("CreateTicket", 0.99f, "User reported bug");

            var result = policy.Evaluate(intent);

            Assert.False(result.IsBlocked);
            Assert.Null(result.RefusalMessage);
        }

        [Fact]
        public void Evaluate_EscalateToHuman_Allowed()
        {
            var policy = new ChatScopePolicy(CreateConfig());
            var intent = new IntentResult("EscalateToHuman", 0.98f, "User wants human");

            var result = policy.Evaluate(intent);

            Assert.False(result.IsBlocked);
            Assert.Null(result.RefusalMessage);
        }

        [Fact]
        public void Evaluate_ClassificationUnavailable_Allowed()
        {
            var policy = new ChatScopePolicy(CreateConfig());
            var intent = new IntentResult("ClassificationUnavailable", 0.0f, "Timeout or error");

            var result = policy.Evaluate(intent);

            Assert.False(result.IsBlocked);
            Assert.Null(result.RefusalMessage);
        }

        [Fact]
        public void Evaluate_NullIntent_Allowed()
        {
            var policy = new ChatScopePolicy(CreateConfig());

            var result = policy.Evaluate(null);

            Assert.False(result.IsBlocked);
            Assert.Null(result.RefusalMessage);
            Assert.Equal("ClassificationUnavailable", result.Intent);
        }

        [Fact]
        public void Evaluate_CustomConfigurableThreshold_Respected()
        {
            var policy = new ChatScopePolicy(CreateConfig("0.85"));
            Assert.Equal(0.85f, policy.OutOfScopeThreshold);

            var intent82 = new IntentResult("OutOfScope", 0.82f, "Below custom threshold");
            var result82 = policy.Evaluate(intent82);
            Assert.False(result82.IsBlocked);

            var intent87 = new IntentResult("OutOfScope", 0.87f, "Above custom threshold");
            var result87 = policy.Evaluate(intent87);
            Assert.True(result87.IsBlocked);
        }

        [Theory]
        [InlineData("what was my first question?")]
        [InlineData("What was my previous question?")]
        [InlineData("what did I ask earlier?")]
        [InlineData("can you repeat what you said?")]
        [InlineData("who are you?")]
        [InlineData("when was this question asked?")]
        [InlineData("at 12:23 what did i ask to you?")]
        [InlineData("what was my first question in this chat?")]
        [InlineData("so do you remember me we had some chatting earlier?")]
        [InlineData("do you remember me?")]
        public void Evaluate_ConversationMetaQueries_NeverBlockedEvenIfClassifiedOutOfScope(string query)
        {
            var policy = new ChatScopePolicy(CreateConfig());
            var falseOutOfScopeIntent = new IntentResult("OutOfScope", 0.95f, "Classifier hallucination");

            var result = policy.Evaluate(falseOutOfScopeIntent, "TheBigLebowski", query);

            Assert.False(result.IsBlocked);
            Assert.Null(result.RefusalMessage);
            Assert.Equal("FAQ", result.Intent);
            Assert.True(policy.IsConversationMetaQuery(query));
        }

        [Theory]
        [InlineData("how to start a direct chat")]
        [InlineData("how to cancel my subscription")]
        [InlineData("how do I change my password")]
        [InlineData("how to block someone")]
        [InlineData("Who is Lionel Messi and Sachin Tendulkar?")]
        public void IsConversationMetaQuery_GenuineQueries_ReturnsFalse(string query)
        {
            var policy = new ChatScopePolicy(CreateConfig());
            Assert.False(policy.IsConversationMetaQuery(query));
        }
    }
}

