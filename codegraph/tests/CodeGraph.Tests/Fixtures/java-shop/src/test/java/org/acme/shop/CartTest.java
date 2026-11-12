package org.acme.shop;

import static org.junit.jupiter.api.Assertions.assertEquals;

import java.util.Map;
import org.junit.jupiter.api.Test;

class CartTest {
    @Test
    void totalAppliesTheDiscount() {
        Cart cart = new Cart(new DiscountPricing(Map.of("apple", 100L), 10));
        cart.add("apple");
        assertEquals(90, cart.total());
    }
}
