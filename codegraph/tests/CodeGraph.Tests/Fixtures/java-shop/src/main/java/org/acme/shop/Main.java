package org.acme.shop;

import java.util.Map;

public class Main {
    public static void main(String[] args) {
        Cart cart = new Cart(new DiscountPricing(Map.of("apple", 100L), 10));
        cart.add("apple");
        System.out.println(cart.total());
    }
}
