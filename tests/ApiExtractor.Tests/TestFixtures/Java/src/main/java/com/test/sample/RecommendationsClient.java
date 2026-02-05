// Copyright (c) Test Corporation.
// Sample Java package for testing API extraction.

package com.test.sample;

/**
 * Interface for recommendations operations.
 */
public interface RecommendationsClient {
    /**
     * Lists recommendations.
     *
     * @return List of recommendations.
     */
    java.util.List<String> listRecommendations();
}
